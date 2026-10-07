using System.IO;
using System.Text.Json;
using System.Windows;

namespace FullStackLauncher.ProjectTasks;

/// <summary>A staged process owns one ordinary Codex turn across dashboard closure and replacement.</summary>
internal static class CodexAgentChatHost
{
    internal static async void Open(string[] args)
    {
        try
        {
            var index = Array.FindIndex(args, arg => arg.Equals("--codex-agent-chat", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("An agent chat receipt is required.");
            await Task.Run(() => RunAsync(args[index + 1]));
        }
        catch (Exception)
        {
            // Hidden owners never open crash dialogs or print prompt contents.
            try { Console.Error.WriteLine("The agent chat owner could not complete its saved request."); }
            catch { }
        }
        finally { Application.Current.Shutdown(); }
    }

    private static async Task RunAsync(string attemptId)
    {
        var store = new CodexAgentChatStore();
        using var claim = store.Claim(attemptId);
        var receipt = store.Read(attemptId);
        // A repeated command can never resend an already started or reviewed attempt.
        if (receipt.State != CodexAgentChatState.Prepared || store.IsReviewed(attemptId)) return;
        CodexAppServerConnection? connection = null;
        var threadCreationAttempted = false;
        var turnSubmissionAttempted = false;
        var submittedInputConfirmed = false;

        void Save(CodexAgentChatState state, string summary, bool terminal = false)
        {
            receipt = receipt with
            {
                State = state, Summary = summary, SubmissionAttempted = turnSubmissionAttempted,
                ThreadCreationAttempted = threadCreationAttempted,
                TerminalConfirmed = terminal, UpdatedAt = DateTimeOffset.UtcNow
            };
            store.Save(receipt);
        }

        void Finish(JsonElement turn)
        {
            if (!submittedInputConfirmed && !CodexAgentImageStaging.MatchesSubmittedInput(turn, receipt, MakePrompt(receipt.Request)))
            {
                Save(CodexAgentChatState.Unconfirmed,
                    "Codex ended the exact turn, but its saved message and images could not be confirmed. Review the chat before sending again.");
                return;
            }
            switch (RequiredString(turn, "status"))
            {
                case "completed":
                    Save(CodexAgentChatState.Completed, "Codex completed the submitted turn. Open the chat to review its response.", true);
                    break;
                case "failed":
                    Save(CodexAgentChatState.Failed, FailureSummary(turn), true);
                    break;
                case "interrupted":
                    Save(CodexAgentChatState.Interrupted, "Codex confirmed that the submitted turn was interrupted.", true);
                    break;
                default:
                    Save(CodexAgentChatState.Unconfirmed, "Codex returned an unsupported turn status. Review this chat before sending again.");
                    break;
            }
        }

        try
        {
            Save(CodexAgentChatState.Starting, "Connecting to Codex for this new chat.");
            var request = receipt.Request;
            if (!Directory.Exists(request.Folder)) throw new InvalidOperationException("The project folder no longer exists.");
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            connection = await CodexAppServerConnection.StartAsync(request.Folder, startup.Token,
                experimentalApi: true, includeImagePayloads: request.Images.Count > 0).ConfigureAwait(false);
            var models = await CodexModelCatalog.LoadAsync(connection, startup.Token).ConfigureAwait(false);
            var model = models.SingleOrDefault(item => string.Equals(item.Id, request.ModelId, StringComparison.Ordinal));
            if (model is null || !model.SupportedReasoningEfforts.Any(item =>
                    string.Equals(item.Id, request.ReasoningEffort, StringComparison.Ordinal)))
                throw new InvalidOperationException("The selected model or thinking level is no longer available. Refresh models before starting a new chat.");

            Save(CodexAgentChatState.Starting, "Checking command access before creating the new chat.");
            await CodexCommandAccessCheck.VerifyAsync(connection, request.Folder, request.AccessMode,
                startup.Token).ConfigureAwait(false);
            var stagedImagePaths = CodexAgentImageStaging.Stage(receipt);

            // Persist intent before writing the first mutating protocol request.
            threadCreationAttempted = true;
            Save(CodexAgentChatState.Starting, "Creating the new Codex chat.");
            var response = await connection.RequestAsync("thread/start", new
            {
                cwd = request.Folder, model = request.ModelId,
                sandbox = CodexAgentAccessPolicy.RequestedSandbox(request.AccessMode),
                approvalPolicy = CodexAgentAccessPolicy.ApprovalPolicy, approvalsReviewer = "user",
                config = new Dictionary<string, object> { ["model_reasoning_effort"] = request.ReasoningEffort }
            }, startup.Token).ConfigureAwait(false);
            receipt = receipt with { ThreadId = RequiredString(RequiredObject(response, "thread"), "id") };
            Save(CodexAgentChatState.Starting, "Chat created. Checking its folder and model before sending.");
            ValidateConfiguration(response, request);
            await WaitForThreadStartedAsync(connection, receipt.ThreadId!, startup.Token).ConfigureAwait(false);
            turnSubmissionAttempted = true;
            Save(CodexAgentChatState.Starting, "Sending the prompt and saved project context.");
            var input = new List<object>
            {
                new { type = "text", text = MakePrompt(request), text_elements = Array.Empty<object>() }
            };
            input.AddRange(stagedImagePaths.Select(path => (object)new { type = "localImage", path }));
            var turnResponse = await connection.RequestAsync("turn/start", new
            {
                threadId = receipt.ThreadId, cwd = request.Folder,
                model = request.ModelId, effort = request.ReasoningEffort,
                input
            }, startup.Token).ConfigureAwait(false);
            receipt = receipt with { TurnId = RequiredString(RequiredObject(turnResponse, "turn"), "id") };
            Save(CodexAgentChatState.Starting, "Codex acknowledged the prompt. Waiting for the exact turn to start.");
            var started = false;
            while (true)
            {
                JsonElement notification;
                using (var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                {
                    try { notification = await connection.ReadNotificationAsync(quiet.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (quiet.IsCancellationRequested)
                    {
                        // Reconcile only this exact turn, without resuming or sending anything.
                        var terminal = await ReadTerminalAsync(connection, receipt).ConfigureAwait(false);
                        if (terminal is { } exact) { Finish(exact); return; }
                        continue;
                    }
                }
                var method = RequiredString(notification, "method");
                if (!notification.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object
                    || OptionalString(parameters, "threadId") != receipt.ThreadId) continue;
                if (method is "turn/started" or "turn/completed")
                {
                    var turn = RequiredObject(parameters, "turn");
                    if (RequiredString(turn, "id") != receipt.TurnId) continue;
                    if (method == "turn/started")
                    {
                        if (RequiredString(turn, "status") != "inProgress")
                            throw new InvalidOperationException("Codex returned an unsupported start status. Review this chat before sending again.");
                        started = true;
                        Save(CodexAgentChatState.Running, "The new agent chat is running in Codex.");
                        await TrySetTitleAsync(connection, receipt).ConfigureAwait(false);
                    }
                    else
                    {
                        if (!started)
                            throw new InvalidOperationException("Codex ended the turn without a matching start event. Review this chat before sending again.");
                        if (!submittedInputConfirmed && !CodexAgentImageStaging.MatchesSubmittedInput(turn, receipt, MakePrompt(request)))
                        {
                            var savedTurn = await ReadTerminalAsync(connection, receipt).ConfigureAwait(false);
                            if (savedTurn is { } exact) turn = exact;
                        }
                        Finish(turn);
                        return;
                    }
                }
                else if (method == "item/completed" && OptionalString(parameters, "turnId") == receipt.TurnId
                    && parameters.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
                    && OptionalString(item, "type") == "userMessage")
                {
                    var submitted = JsonSerializer.SerializeToElement(new { items = new[] { item } });
                    if (!CodexAgentImageStaging.MatchesSubmittedInput(submitted, receipt, MakePrompt(request)))
                        throw new InvalidOperationException("Codex returned a message that did not match the saved text and images. Review this chat before sending again.");
                    submittedInputConfirmed = true;
                }
                else if (method == "error" && parameters.TryGetProperty("willRetry", out var retry)
                    && retry.ValueKind == JsonValueKind.False)
                    throw new InvalidOperationException("Codex reported an error without confirming the exact turn ended. Review this chat before sending again. " +
                        (parameters.TryGetProperty("error", out var error) ? CodexFailureDetails.Describe(error) : ""));
            }
        }
        catch (Exception ex)
        {
            var detail = ex switch
            {
                CodexInteractionRequiredException => ex.Message,
                OperationCanceledException => "Codex did not confirm submission before the connection timed out. Review recent tasks in Codex before sending again.",
                InvalidOperationException => ex.Message,
                _ => "The Codex connection or saved receipt became unavailable. Review recent tasks in Codex before sending again."
            };
            // A request, transport, or save failure cannot prove the task stopped.
            // Even a thread without an acknowledged turn remains visible for review.
            var uncertain = turnSubmissionAttempted || (threadCreationAttempted && receipt.ThreadId is null);
            if (!uncertain && receipt.ThreadId is not null && !turnSubmissionAttempted)
                detail += " No prompt was sent. Start a fresh chat when ready.";
            try { Save(uncertain ? CodexAgentChatState.Unconfirmed : CodexAgentChatState.Failed, detail); }
            catch (Exception) { }
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            // A submitted image may still be needed after transport loss or
            // manual continuation. Retain uncertain attempts for explicit review.
            if (!turnSubmissionAttempted || receipt.TerminalConfirmed)
                CodexAgentImageStaging.Cleanup(receipt);
        }
    }

    private static string MakePrompt(CodexAgentChatRequest request)
    {
        var prompt = string.IsNullOrWhiteSpace(request.Prompt) && request.Images.Count > 0
            ? "Review the attached images." : request.Prompt;
        return string.IsNullOrWhiteSpace(request.Context) ? prompt
            : "Saved project context:\n" + request.Context + "\n\nUser request:\n" + prompt;
    }

    private static void ValidateConfiguration(JsonElement response, CodexAgentChatRequest request)
    {
        var cwd = RequiredString(response, "cwd");
        if (!Path.IsPathFullyQualified(cwd)
            || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)), request.Folder, StringComparison.OrdinalIgnoreCase)
            || RequiredString(response, "model") != request.ModelId
            || OptionalString(response, "reasoningEffort") != request.ReasoningEffort
            || RequiredString(response, "approvalPolicy") != CodexAgentAccessPolicy.ApprovalPolicy
            || OptionalString(response, "approvalsReviewer") != "user"
            || RequiredString(RequiredObject(response, "sandbox"), "type") != CodexAgentAccessPolicy.EffectiveSandbox(request.AccessMode))
            throw new InvalidOperationException("Codex did not confirm the requested folder, model, thinking level, access mode, and noninteractive approval policy. No prompt was sent.");
    }

    private static async Task WaitForThreadStartedAsync(CodexAppServerConnection connection, string threadId, CancellationToken token)
    {
        while (true)
        {
            var notification = await connection.ReadNotificationAsync(token).ConfigureAwait(false);
            if (RequiredString(notification, "method") != "thread/started") continue;
            var thread = RequiredObject(RequiredObject(notification, "params"), "thread");
            if (RequiredString(thread, "id") == threadId) return;
        }
    }

    private static async Task<JsonElement?> ReadTerminalAsync(CodexAppServerConnection connection, CodexAgentChatReceipt receipt)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        JsonElement result;
        try
        {
            result = await connection.RequestAsync("thread/read", new { threadId = receipt.ThreadId, includeTurns = false }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CodexRequestException or OperationCanceledException) { return null; }
        var thread = RequiredObject(result, "thread");
        if (RequiredString(thread, "id") != receipt.ThreadId
            || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(RequiredString(thread, "cwd"))), receipt.Request.Folder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < 20; page++)
        {
            JsonElement resultPage;
            try
            {
                resultPage = await connection.RequestAsync("thread/turns/list", new
                {
                    threadId = receipt.ThreadId, limit = 25, sortDirection = "desc", itemsView = "full", cursor
                }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CodexRequestException or OperationCanceledException) { return null; }
            if (!resultPage.TryGetProperty("data", out var turns) || turns.ValueKind != JsonValueKind.Array
                || turns.GetArrayLength() > 25) throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
            foreach (var turn in turns.EnumerateArray())
                if (RequiredString(turn, "id") == receipt.TurnId)
                    return RequiredString(turn, "status") is "completed" or "failed" or "interrupted" ? turn.Clone() : null;
            cursor = OptionalString(resultPage, "nextCursor");
            if (cursor is null) return null;
            if (!cursors.Add(cursor)) throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
        }
        return null;
    }

    private static async Task TrySetTitleAsync(CodexAppServerConnection connection, CodexAgentChatReceipt receipt)
    {
        var firstLine = receipt.Request.Prompt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(firstLine)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await connection.RequestAsync("thread/name/set", new { threadId = receipt.ThreadId, name = firstLine[..Math.Min(firstLine.Length, 80)] }, timeout.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { }
    }

    private static JsonElement RequiredObject(JsonElement value, string name) =>
        value.TryGetProperty(name, out var result) && result.ValueKind == JsonValueKind.Object
            ? result : throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
    private static string RequiredString(JsonElement value, string name) => OptionalString(value, name)
        ?? throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
    private static string? OptionalString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var result) && result.ValueKind == JsonValueKind.String
            ? result.GetString() : null;

    private static string FailureSummary(JsonElement turn)
    {
        return turn.TryGetProperty("error", out var error)
            ? CodexFailureDetails.Describe(error)
            : "Codex reported that the submitted turn failed. Open the chat to review it.";
    }
}
