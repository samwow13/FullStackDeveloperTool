using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace FullStackLauncher.ProjectTasks;

public enum CodexCheckState { Starting, ThreadCreated, Running, Completed, Blocked, NeedsInput, Interrupted, Failed, Unknown }

public sealed record CodexCheckUpdate(
    CodexCheckState State, string Folder, string ModelId, string ReasoningEffort,
    string? ThreadId, string? TurnId, string Summary, IReadOnlyList<string> InstructionSourcePaths,
    bool TerminalConfirmed = false);

public sealed record CodexCheckResult(
    CodexCheckState State, string Folder, string ModelId, string ReasoningEffort,
    string? ThreadId, string? TurnId, string Summary, IReadOnlyList<string> InstructionSourcePaths,
    bool TerminalConfirmed = false);

/// <summary>
/// A single explicit, bounded integration check. It cannot accept note text or advance a queue.
/// The caller must durably save its frozen check intent before invoking this service.
/// </summary>
public sealed class CodexConnectionCheck
{
    public const string Prompt = "This is a bounded Full Stack Launcher connection check. "
        + "Do not perform project work. Do not use tools, run commands, browse, read additional files, "
        + "write or edit files, invoke skills, create goals, delegate, or send messages. "
        + "Reply directly with one JSON object matching the supplied output schema. "
        + "Use outcome completed and summary 'Connection check completed; no project work requested.' "
        + "if you can answer directly. If you cannot, report blocked or needs-input with a brief explanation. "
        + "Do not claim desktop sidebar association or project correctness has been verified.";

    private readonly object _sync = new();
    private readonly SemaphoreSlim _interruptLock = new(1, 1);
    private CodexAppServerConnection? _connection;
    private string? _threadId;
    private string? _turnId;
    private bool _stopRequested;
    private bool _interruptSent;
    private bool _finished;
    private int _used;

    public string? ThreadId { get { lock (_sync) return _threadId; } }
    public string? TurnId { get { lock (_sync) return _turnId; } }

    public async Task<CodexCheckResult> CheckAsync(string folder, string modelId, string effort,
        Func<CodexCheckUpdate, Task> persistUpdate, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0)
            throw new InvalidOperationException("Create a new connection check for each explicit attempt.");
        ArgumentNullException.ThrowIfNull(persistUpdate);
        var resolvedFolder = folder;
        var resolvedModel = modelId;
        var resolvedEffort = effort;
        IReadOnlyList<string> instructionPaths = Array.Empty<string>();
        var threadSubmissionAttempted = false;
        var turnSubmissionAttempted = false;
        var callbackFailed = false;
        var terminalConfirmed = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var token = timeout.Token;

        CodexCheckResult Result(CodexCheckState state, string summary) =>
            new(state, resolvedFolder, resolvedModel, resolvedEffort, ThreadId, TurnId, summary, instructionPaths, terminalConfirmed);

        async Task PersistAsync(CodexCheckState state, string summary)
        {
            try
            {
                // No external submission may follow a failed or canceled persistence callback.
                await persistUpdate(new(state, resolvedFolder, resolvedModel, resolvedEffort,
                    ThreadId, TurnId, summary, instructionPaths)).ConfigureAwait(false);
            }
            catch { callbackFailed = true; throw; }
            // Finish the ordered local write even if cancellation arrives while the dispatcher is saving.
            token.ThrowIfCancellationRequested();
        }

        try
        {
            token.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
                return Result(CodexCheckState.Failed, "The assigned project folder must be an existing absolute folder.");
            resolvedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(effort))
                return Result(CodexCheckState.Failed, "Select a discovered model and thinking level before running the check.");
            if (IsStopRequested())
                return Result(CodexCheckState.Interrupted, "Stopped before creating a Codex task.");

            var connection = await CodexAppServerConnection.StartAsync(resolvedFolder, token).ConfigureAwait(false);
            lock (_sync) _connection = connection;
            var catalog = await CodexModelCatalog.LoadAsync(connection, token).ConfigureAwait(false);
            var selected = catalog.SingleOrDefault(model => string.Equals(model.Id, modelId, StringComparison.Ordinal));
            if (selected is null || !selected.SupportedReasoningEfforts.Any(option => string.Equals(option.Id, effort, StringComparison.Ordinal)))
                return Result(CodexCheckState.Failed, "The selected model or thinking level is no longer available. Refresh models and explicitly try again.");

            await PersistAsync(CodexCheckState.Starting, "Model and thinking level revalidated. Creating a bounded connection-check task.").ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (IsStopRequested())
                return Result(CodexCheckState.Interrupted, "Stopped before creating a Codex task.");
            var threadResponse = await connection.RequestAsync("thread/start", new
            {
                cwd = resolvedFolder, model = modelId, sandbox = "read-only", approvalPolicy = "never",
                config = new Dictionary<string, object> { ["model_reasoning_effort"] = effort }
            }, token, () =>
            {
                // Claim submission under the same lock as Stop, immediately before the pipe write.
                lock (_sync)
                {
                    if (_stopRequested) throw new CheckStoppedBeforeSubmissionException();
                    threadSubmissionAttempted = true;
                }
            }).ConfigureAwait(false);
            var thread = RequiredObject(threadResponse, "thread");
            lock (_sync) _threadId = RequiredString(thread, "id");

            // Save the returned identity even when its configuration is incompatible with the intent.
            var effectiveFolder = RequiredString(threadResponse, "cwd");
            resolvedModel = RequiredString(threadResponse, "model");
            resolvedEffort = OptionalString(threadResponse, "reasoningEffort") ?? "unreported";
            instructionPaths = ReadInstructionPaths(threadResponse);
            var folderMatches = Path.IsPathFullyQualified(effectiveFolder)
                && string.Equals(resolvedFolder, Path.TrimEndingDirectorySeparator(Path.GetFullPath(effectiveFolder)), StringComparison.OrdinalIgnoreCase);
            resolvedFolder = effectiveFolder;
            await PersistAsync(CodexCheckState.ThreadCreated, "Codex task created. Checking its effective folder, model, thinking level, and permissions.").ConfigureAwait(false);
            if (!folderMatches || !string.Equals(resolvedModel, modelId, StringComparison.Ordinal)
                || !string.Equals(resolvedEffort, effort, StringComparison.Ordinal))
                return Result(CodexCheckState.Blocked, "Codex resolved a different or unreported folder, model, or thinking level. No prompt was submitted.");
            var sandbox = RequiredObject(threadResponse, "sandbox");
            if (RequiredString(threadResponse, "approvalPolicy") != "never" || RequiredString(sandbox, "type") != "readOnly"
                || (sandbox.TryGetProperty("networkAccess", out var network) && network.ValueKind != JsonValueKind.False))
                return Result(CodexCheckState.Blocked, "Codex did not confirm the requested read-only, restricted-network, never-approve policy. No prompt was submitted.");

            // thread/started can precede the response. The transport retains it until this reader consumes it.
            await WaitForThreadStartedAsync(connection, ThreadId!, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (IsStopRequested())
                return Result(CodexCheckState.Interrupted, "Stopped after task creation and before prompt submission.");

            var turnResponse = await connection.RequestAsync("turn/start", new
            {
                threadId = ThreadId, cwd = resolvedFolder, model = modelId, effort,
                approvalPolicy = "never", sandboxPolicy = new { type = "readOnly", networkAccess = false },
                input = new[] { new { type = "text", text = Prompt, text_elements = Array.Empty<object>() } },
                outputSchema = new
                {
                    type = "object", additionalProperties = false,
                    properties = new
                    {
                        outcome = new { type = "string", @enum = new[] { "completed", "blocked", "needs-input" } },
                        summary = new { type = "string" }
                    },
                    required = new[] { "outcome", "summary" }
                }
            }, token, () =>
            {
                lock (_sync)
                {
                    if (_stopRequested) throw new CheckStoppedBeforeSubmissionException();
                    turnSubmissionAttempted = true;
                }
            }).ConfigureAwait(false);
            var acknowledgedTurn = RequiredObject(turnResponse, "turn");
            lock (_sync) _turnId = RequiredString(acknowledgedTurn, "id");
            await PersistAsync(CodexCheckState.ThreadCreated, "Codex acknowledged the prompt and returned a turn ID. Waiting for the exact start event.").ConfigureAwait(false);
            if (IsStopRequested())
                await SendInterruptAsync(token).ConfigureAwait(false);

            var started = false;
            var finalMessages = new Dictionary<string, string>(StringComparer.Ordinal);
            while (true)
            {
                var notification = await connection.ReadNotificationAsync(token).ConfigureAwait(false);
                var method = RequiredString(notification, "method");
                var parameters = RequiredObject(notification, "params");
                if (!string.Equals(OptionalString(parameters, "threadId"), ThreadId, StringComparison.Ordinal))
                    continue;
                if (method is "turn/started" or "turn/completed")
                {
                    var turn = RequiredObject(parameters, "turn");
                    if (!string.Equals(RequiredString(turn, "id"), TurnId, StringComparison.Ordinal))
                        continue;
                    var status = RequiredString(turn, "status");
                    if (method == "turn/started")
                    {
                        if (status != "inProgress")
                            return Result(CodexCheckState.Unknown, "The exact turn start event had an unsupported status. No successful completion was inferred.");
                        if (!started)
                        {
                            started = true;
                            await PersistAsync(CodexCheckState.Running, "The expected Codex task and turn have started.").ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        if (!started)
                            return Result(CodexCheckState.Unknown, "A terminal event arrived without the expected start acknowledgment. The check needs review.");
                        if (status == "interrupted")
                        {
                            terminalConfirmed = true;
                            return Result(CodexCheckState.Interrupted, "Codex confirmed that the exact check turn was interrupted.");
                        }
                        if (status == "failed")
                        {
                            terminalConfirmed = true;
                            return Result(CodexCheckState.Failed, "Codex reported that the exact check turn failed. Review the task in Codex for details.");
                        }
                        if (status != "completed")
                            return Result(CodexCheckState.Unknown, "Codex returned an unsupported terminal status. No successful completion was inferred.");
                        terminalConfirmed = true;
                        CollectTurnMessages(turn, finalMessages);
                        var outcome = ParseOutcome(finalMessages);
                        return Result(outcome.State, outcome.Summary);
                    }
                }
                else if (method is "item/started" or "item/completed")
                {
                    if (!string.Equals(OptionalString(parameters, "turnId"), TurnId, StringComparison.Ordinal))
                        continue;
                    var item = RequiredObject(parameters, "item");
                    EnsureReplyOnlyItem(item);
                    if (method == "item/completed") CollectMessage(item, finalMessages);
                }
                else if (method == "error" && parameters.TryGetProperty("willRetry", out var willRetry) && willRetry.ValueKind == JsonValueKind.False)
                {
                    await TryInterruptAsync().ConfigureAwait(false);
                    return Result(CodexCheckState.Unknown, "Codex reported an error without confirming the exact turn's terminal state. Review the task in Codex.");
                }
            }
        }
        catch (CheckStoppedBeforeSubmissionException)
        {
            return Result(CodexCheckState.Interrupted, "Stopped before prompt submission. No connection-check turn was sent.");
        }
        catch (CodexInteractionRequiredException ex)
        {
            await TryInterruptAsync().ConfigureAwait(false);
            return Result(CodexCheckState.NeedsInput, ex.Message);
        }
        catch (Exception) when (callbackFailed)
        {
            await TryInterruptAsync().ConfigureAwait(false);
            return Result(threadSubmissionAttempted ? CodexCheckState.Unknown : CodexCheckState.Failed,
                "The check receipt could not be saved. Further submission stopped; any known task and turn IDs must be retained for review.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            await TryInterruptAsync().ConfigureAwait(false);
            return Result(turnSubmissionAttempted || (threadSubmissionAttempted && ThreadId is null) ? CodexCheckState.Unknown : CodexCheckState.Interrupted,
                cancellationToken.IsCancellationRequested
                    ? "The connection check was canceled. Any submitted turn remains unconfirmed until reviewed in Codex."
                    : "The 90-second connection-check limit was reached. Any submitted turn remains unconfirmed until reviewed in Codex.");
        }
        catch (CodexRequestException ex)
        {
            return Result(TurnId is null ? CodexCheckState.Failed : CodexCheckState.Unknown, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or Win32Exception
            or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            await TryInterruptAsync().ConfigureAwait(false);
            return Result(threadSubmissionAttempted ? CodexCheckState.Unknown : CodexCheckState.Failed,
                threadSubmissionAttempted
                    ? "Codex connection or protocol evidence was lost after task creation began. Do not retry this attempt automatically; review the retained IDs in Codex."
                    : CodexAppServerConnection.UnavailableMessage);
        }
        finally
        {
            CodexAppServerConnection? owned;
            lock (_sync) { _finished = true; owned = _connection; _connection = null; }
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stops future check submission, or requests interruption of this service's exact known turn.</summary>
    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync) _stopRequested = true;
        await SendInterruptAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendInterruptAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _interruptLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            CodexAppServerConnection? connection;
            string? threadId;
            string? turnId;
            lock (_sync)
            {
                if (_finished || _interruptSent || _turnId is null) return;
                connection = _connection; threadId = _threadId; turnId = _turnId;
            }
            if (connection is null || threadId is null) return;
            await connection.RequestAsync("turn/interrupt", new { threadId, turnId }, timeout.Token).ConfigureAwait(false);
            lock (_sync) _interruptSent = true;
            // An interrupt acknowledgment is not an interrupted terminal result.
        }
        finally { _interruptLock.Release(); }
    }

    private async Task TryInterruptAsync()
    {
        try { await SendInterruptAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException) { }
    }

    private bool IsStopRequested() { lock (_sync) return _stopRequested; }

    private sealed class CheckStoppedBeforeSubmissionException : Exception { }

    private static async Task WaitForThreadStartedAsync(CodexAppServerConnection connection, string threadId, CancellationToken token)
    {
        while (true)
        {
            var notification = await connection.ReadNotificationAsync(token).ConfigureAwait(false);
            if (RequiredString(notification, "method") != "thread/started") continue;
            var thread = RequiredObject(RequiredObject(notification, "params"), "thread");
            if (string.Equals(RequiredString(thread, "id"), threadId, StringComparison.Ordinal)) return;
        }
    }

    private static IReadOnlyList<string> ReadInstructionPaths(JsonElement response)
    {
        if (!response.TryGetProperty("instructionSources", out var sources)) return Array.Empty<string>();
        if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() > 256)
            throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
        var paths = new List<string>();
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.String || source.GetString() is not { Length: > 0 and <= 32768 } path)
                throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
            paths.Add(path);
        }
        return paths.AsReadOnly();
    }

    private static void EnsureReplyOnlyItem(JsonElement item)
    {
        if (RequiredString(item, "type") is not ("userMessage" or "agentMessage" or "reasoning" or "plan"))
            throw new CodexInteractionRequiredException();
        if (item.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array && questions.GetArrayLength() > 0)
            throw new CodexInteractionRequiredException();
        if (OptionalString(item, "delivery") is not null)
            throw new CodexInteractionRequiredException();
    }

    private static void CollectTurnMessages(JsonElement turn, Dictionary<string, string> messages)
    {
        if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
        foreach (var item in items.EnumerateArray())
        {
            EnsureReplyOnlyItem(item);
            CollectMessage(item, messages);
        }
    }

    private static void CollectMessage(JsonElement item, Dictionary<string, string> messages)
    {
        if (RequiredString(item, "type") != "agentMessage") return;
        var phase = OptionalString(item, "phase");
        if (phase == "commentary") return;
        if (phase is not null and not "final_answer")
            throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
        if (messages.Count >= 8)
            throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
        var text = RequiredString(item, "text");
        if (text.Length > 16384)
            throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
        messages[RequiredString(item, "id")] = text;
    }

    private static (CodexCheckState State, string Summary) ParseOutcome(Dictionary<string, string> messages)
    {
        if (messages.Count != 1)
            return (CodexCheckState.Unknown, "Codex completed the turn without one unambiguous structured final response. Review it in Codex.");
        using var response = JsonDocument.Parse(messages.Values.Single());
        var root = response.RootElement;
        var outcome = RequiredString(root, "outcome");
        var summary = RequiredString(root, "summary");
        if (root.EnumerateObject().Count() != 2 || summary.Length > 2000)
            throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
        var state = outcome switch
        {
            "completed" => CodexCheckState.Completed,
            "blocked" => CodexCheckState.Blocked,
            "needs-input" => CodexCheckState.NeedsInput,
            _ => CodexCheckState.Unknown
        };
        return state == CodexCheckState.Unknown
            ? (state, "Codex returned an unknown reported outcome. Review the final response in Codex.")
            : (state, summary);
    }

    private static JsonElement RequiredObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
        return value;
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) ?? throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);

    private static string? OptionalString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
        return string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString();
    }
}
