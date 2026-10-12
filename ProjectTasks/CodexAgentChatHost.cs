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
        var keepAwake = new CodexAgentKeepAwakeStore();
        var keepAwakeInitialized = false;
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
            keepAwake.Initialize(attemptId, request.ProjectId, receipt.ThreadId!, request.KeepAwake);
            keepAwakeInitialized = true;
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
            keepAwake.SetTurn(attemptId, request.ProjectId, receipt.ThreadId!, receipt.TurnId!, running: false);
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
                        if (terminal is { } exact)
                        {
                            Finish(exact);
                            await ContinueIfEnabledAsync(connection, receipt, keepAwake).ConfigureAwait(false);
                            return;
                        }
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
                        keepAwake.SetTurn(attemptId, request.ProjectId, receipt.ThreadId!, receipt.TurnId!, running: true);
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
                        await ContinueIfEnabledAsync(connection, receipt, keepAwake).ConfigureAwait(false);
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
            try
            {
                if (!receipt.TerminalConfirmed)
                    Save(uncertain ? CodexAgentChatState.Unconfirmed : CodexAgentChatState.Failed, detail);
                if (keepAwakeInitialized)
                    keepAwake.Suspend(attemptId, receipt.Request.ProjectId, receipt.ThreadId!,
                        "Keep awake suspended. " + detail);
            }
            catch (Exception) { }
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            if (keepAwakeInitialized)
            {
                try { keepAwake.Close(attemptId, receipt.Request.ProjectId, receipt.ThreadId!); }
                catch (Exception) { }
            }
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

    private static async Task ContinueIfEnabledAsync(CodexAppServerConnection connection,
        CodexAgentChatReceipt original, CodexAgentKeepAwakeStore keepAwake)
    {
        var projectId = original.Request.ProjectId;
        var threadId = original.ThreadId!;
        var ownerAttemptId = original.AttemptId;
        if (original.State != CodexAgentChatState.Completed || !original.TerminalConfirmed)
        {
            keepAwake.Suspend(ownerAttemptId, projectId, threadId,
                "Keep awake suspended. The initial turn failed, was interrupted, or requires review.");
            return;
        }
        var predecessorTurnId = original.TurnId!;
        var predecessorPrompt = MakePrompt(original.Request);
        var predecessorIsInitial = true;
        while (true)
        {
            CodexAgentKeepAwakeStore.ContinuationIntent? intent = null;
            string? continuationTurnId = null;
            var terminalRecorded = false;
            try
            {
                keepAwake.SetIdle(ownerAttemptId, projectId, threadId, predecessorTurnId);
                if (!keepAwake.ContinueOrCloseIfDisabled(ownerAttemptId, projectId, threadId)) return;
                // Avoid rapid finished/continue loops and give the developer time to disable the next submission.
                await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                if (!keepAwake.ContinueOrCloseIfDisabled(ownerAttemptId, projectId, threadId)) return;
                using var submission = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await VerifyReadyForContinuationAsync(connection, original, predecessorTurnId,
                    predecessorPrompt, predecessorIsInitial, submission.Token).ConfigureAwait(false);
                using (keepAwake.AcquireCommand(projectId, threadId))
                {
                    intent = keepAwake.PrepareContinuationUnderCommand(ownerAttemptId, projectId,
                        threadId, predecessorTurnId);
                    if (intent is null) return;
                    // Hold the exact-chat command gate through write and acknowledgment. An Off save cannot race the write.
                    var response = await connection.RequestAsync("turn/start", new
                    {
                        threadId, cwd = original.Request.Folder, model = original.Request.ModelId,
                        effort = original.Request.ReasoningEffort,
                        input = new[] { new { type = "text", text = "Keep Going", text_elements = Array.Empty<object>() } }
                    }, submission.Token, () => keepAwake.BeforeSubmissionUnderCommand(intent)).ConfigureAwait(false);
                    var accepted = RequiredObject(response, "turn");
                    continuationTurnId = RequiredString(accepted, "id");
                    if (RequiredString(accepted, "status") != "inProgress")
                        throw new InvalidOperationException("Codex did not confirm the continuation as an active turn.");
                    keepAwake.RecordAcceptedUnderCommand(intent, continuationTurnId);
                }
                var exactTurn = await WaitForContinuationAsync(connection, original, predecessorTurnId, continuationTurnId,
                    keepAwake).ConfigureAwait(false);
                var status = RequiredString(exactTurn, "status");
                if (!MatchesContinuationInput(exactTurn))
                    throw new InvalidOperationException("Codex ended the exact continuation, but its Keep Going input could not be confirmed. Review this chat before continuing.");
                var summary = status switch
                {
                    "completed" => "Codex completed the exact Keep Going turn.",
                    "interrupted" => "Codex confirmed that the exact Keep Going turn was interrupted.",
                    "failed" => FailureSummary(exactTurn),
                    _ => throw new InvalidOperationException("Codex returned an unsupported continuation status. Review this chat before continuing.")
                };
                keepAwake.RecordOutcome(intent, continuationTurnId, status, summary, terminalConfirmed: true);
                terminalRecorded = true;
                if (status != "completed")
                {
                    keepAwake.Suspend(ownerAttemptId, projectId, threadId, "Keep awake suspended. " + summary);
                    return;
                }
                predecessorTurnId = continuationTurnId;
                predecessorPrompt = "Keep Going";
                predecessorIsInitial = false;
            }
            catch (Exception ex)
            {
                var detail = ex switch
                {
                    CodexInteractionRequiredException => ex.Message,
                    InvalidOperationException => ex.Message,
                    OperationCanceledException => "The exact continuation could not be confirmed before its connection timed out. Review this chat before continuing.",
                    _ => "The continuation connection or receipt became unavailable. Existing work may still be running. Review this chat before continuing."
                };
                if (intent is not null && !terminalRecorded)
                {
                    try { keepAwake.RecordOutcome(intent, continuationTurnId, "unconfirmed", detail, terminalConfirmed: false); }
                    catch (Exception) { }
                }
                try { keepAwake.Suspend(ownerAttemptId, projectId, threadId, "Keep awake suspended. " + detail); }
                catch (Exception) { }
                return;
            }
        }
    }

    private static async Task VerifyReadyForContinuationAsync(CodexAppServerConnection connection,
        CodexAgentChatReceipt original, string predecessorTurnId, string predecessorPrompt,
        bool predecessorIsInitial, CancellationToken token)
    {
        var response = await connection.RequestAsync("thread/read", new
        {
            threadId = original.ThreadId, includeTurns = false
        }, token).ConfigureAwait(false);
        var thread = RequiredObject(response, "thread");
        if (RequiredString(thread, "id") != original.ThreadId
            || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(RequiredString(thread, "cwd"))),
                original.Request.Folder, StringComparison.OrdinalIgnoreCase)
            || RequiredString(RequiredObject(thread, "status"), "type") != "idle")
            throw new InvalidOperationException("Keep awake stopped because the exact chat was not confirmed idle in its original folder.");
        var turnsResponse = await connection.RequestAsync("thread/turns/list", new
        {
            threadId = original.ThreadId, limit = 1, sortDirection = "desc", itemsView = "full"
        }, token).ConfigureAwait(false);
        if (!turnsResponse.TryGetProperty("data", out var turns) || turns.ValueKind != JsonValueKind.Array
            || turns.GetArrayLength() != 1)
            throw new InvalidOperationException("Keep awake could not verify the chat's latest completed turn.");
        var latest = turns[0];
        if (RequiredString(latest, "id") != predecessorTurnId || RequiredString(latest, "status") != "completed"
            || OptionalString(latest, "itemsView") is { } view && view != "full")
            throw new InvalidOperationException("Keep awake stopped because another turn or incomplete history replaced its completed turn.");
        if (!(predecessorIsInitial
                ? CodexAgentImageStaging.MatchesSubmittedInput(latest, original, predecessorPrompt)
                : MatchesContinuationInput(latest)))
            throw new InvalidOperationException("Keep awake could not confirm the completed turn's exact input. Review this chat before continuing.");
        if (!HasSettledWork(latest, original.ThreadId!))
            throw new InvalidOperationException("Keep awake suspended because a tool, input request, or child agent lacks confirmed final status.");
    }

    private static async Task<JsonElement> WaitForContinuationAsync(CodexAppServerConnection connection,
        CodexAgentChatReceipt original, string predecessorTurnId, string turnId, CodexAgentKeepAwakeStore keepAwake)
    {
        var started = false;
        var inputConfirmed = false;
        var exactReceipt = original with { TurnId = turnId };
        while (true)
        {
            JsonElement notification;
            using (var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
            {
                try { notification = await connection.ReadNotificationAsync(quiet.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (quiet.IsCancellationRequested)
                {
                    var terminal = await ReadTerminalAsync(connection, exactReceipt).ConfigureAwait(false);
                    if (terminal is null) continue;
                    if (!started) throw new InvalidOperationException("The continuation ended without its exact start event. Review this chat before continuing.");
                    return terminal.Value;
                }
            }
            var method = RequiredString(notification, "method");
            if (!notification.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object
                || OptionalString(parameters, "threadId") != original.ThreadId) continue;
            if (method is "turn/started" or "turn/completed")
            {
                var turn = RequiredObject(parameters, "turn");
                if (method == "turn/completed" && RequiredString(turn, "id") == predecessorTurnId)
                    continue; // Read-only quiet reconciliation can run before its already-confirmed terminal notification arrives.
                if (RequiredString(turn, "id") != turnId)
                    throw new InvalidOperationException("Another turn started on this chat. Keep awake will not submit further work.");
                if (method == "turn/started")
                {
                    if (RequiredString(turn, "status") != "inProgress")
                        throw new InvalidOperationException("Codex returned an unsupported continuation start status.");
                    started = true;
                    keepAwake.SetTurn(original.AttemptId, original.Request.ProjectId, original.ThreadId!, turnId, running: true);
                }
                else
                {
                    if (!started) throw new InvalidOperationException("The continuation ended without its exact start event. Review this chat before continuing.");
                    if (!inputConfirmed || !MatchesContinuationInput(turn))
                    {
                        var stored = await ReadTerminalAsync(connection, exactReceipt).ConfigureAwait(false);
                        if (stored is { } exact) turn = exact;
                    }
                    return turn;
                }
            }
            else if (method == "item/completed" && OptionalString(parameters, "turnId") == turnId
                && parameters.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
                && OptionalString(item, "type") == "userMessage")
            {
                var submitted = JsonSerializer.SerializeToElement(new { items = new[] { item } });
                if (!MatchesContinuationInput(submitted))
                    throw new InvalidOperationException("Codex returned continuation input different from Keep Going. Review this chat before continuing.");
                inputConfirmed = true;
            }
            else if (method == "error" && parameters.TryGetProperty("willRetry", out var retry)
                && retry.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException("Codex reported a continuation error without confirming the exact turn ended. "
                    + (parameters.TryGetProperty("error", out var error) ? CodexFailureDetails.Describe(error) : ""));
        }
    }

    private static bool MatchesContinuationInput(JsonElement turn)
    {
        if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return false;
        var messages = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (OptionalString(item, "type") != "userMessage") continue;
            if (++messages != 1 || !item.TryGetProperty("content", out var contents)
                || contents.ValueKind != JsonValueKind.Array || contents.GetArrayLength() != 1
                || OptionalString(contents[0], "type") != "text" || OptionalString(contents[0], "text") != "Keep Going")
                return false;
        }
        return messages == 1;
    }

    private static bool HasSettledWork(JsonElement turn, string threadId)
    {
        if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() > 2048) return false;
        var children = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            var type = OptionalString(item, "type");
            if (type == "collabAgentToolCall")
            {
                if (OptionalString(item, "senderThreadId") != threadId
                    || OptionalString(item, "status") != "completed"
                    || !item.TryGetProperty("receiverThreadIds", out var receivers) || receivers.ValueKind != JsonValueKind.Array
                    || receivers.GetArrayLength() > 64 || !item.TryGetProperty("agentsStates", out var states)
                    || states.ValueKind != JsonValueKind.Object) return false;
                foreach (var receiver in receivers.EnumerateArray())
                {
                    if (receiver.ValueKind != JsonValueKind.String || receiver.GetString() is not { Length: > 0 } childId) return false;
                    children[childId] = states.TryGetProperty(childId, out var state) && state.ValueKind == JsonValueKind.Object
                        && OptionalString(state, "status") is "completed" or "shutdown";
                }
            }
            else if (type == "subAgentActivity")
            {
                var childId = OptionalString(item, "agentThreadId");
                if (string.IsNullOrWhiteSpace(childId)) return false;
                children[childId] = OptionalString(item, "kind") == "completed";
            }
            else if (type is "commandExecution" or "fileChange" or "mcpToolCall" or "dynamicToolCall")
            {
                if (OptionalString(item, "status") is not ("completed" or "failed")) return false;
            }
            else if (type is not ("userMessage" or "agentMessage" or "reasoning" or "plan" or "webSearch"
                or "imageView" or "enteredReviewMode" or "exitedReviewMode" or "contextCompaction")) return false;
        }
        return children.Values.All(settled => settled);
    }

    private static void ValidateConfiguration(JsonElement response, CodexAgentChatRequest request)
    {
        var cwd = RequiredString(response, "cwd");
        if (!Path.IsPathFullyQualified(cwd)
            || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)), request.Folder, StringComparison.OrdinalIgnoreCase)
            || RequiredString(response, "model") != request.ModelId
            || !MatchesOptionalConfiguration(response, "reasoningEffort", request.ReasoningEffort, allowNull: true)
            || RequiredString(response, "approvalPolicy") != CodexAgentAccessPolicy.ApprovalPolicy
            || !MatchesOptionalConfiguration(response, "approvalsReviewer", "user")
            || RequiredString(RequiredObject(response, "sandbox"), "type") != CodexAgentAccessPolicy.EffectiveSandbox(request.AccessMode))
            throw new InvalidOperationException("Codex did not confirm the requested folder, model, thinking level, access mode, and noninteractive approval policy. No prompt was sent.");
    }

    private static bool MatchesOptionalConfiguration(JsonElement response, string name, string expected,
        bool allowNull = false)
    {
        // Older responses omit the reviewer; reasoning effort is nullable in the
        // documented protocol. turn/start still sends the exact selected effort.
        // A reported value must match, and malformed values never establish access.
        return !response.TryGetProperty(name, out var value)
            || (allowNull && value.ValueKind == JsonValueKind.Null)
            || (value.ValueKind == JsonValueKind.String && value.GetString() == expected);
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
            catch (CodexRequestException ex) when (ex.IsMethodUnavailable)
            {
                // A rejected read-only pagination method cannot have submitted
                // work. Read legacy history without resuming or replaying a turn.
                return await ReadLegacyTerminalAsync(connection, receipt, timeout.Token).ConfigureAwait(false);
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

    private static async Task<JsonElement?> ReadLegacyTerminalAsync(CodexAppServerConnection connection,
        CodexAgentChatReceipt receipt, CancellationToken token)
    {
        JsonElement response;
        try
        {
            response = await connection.RequestAsync("thread/read", new
            {
                threadId = receipt.ThreadId, includeTurns = true
            }, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CodexRequestException or OperationCanceledException) { return null; }
        var thread = RequiredObject(response, "thread");
        if (RequiredString(thread, "id") != receipt.ThreadId
            || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(RequiredString(thread, "cwd"))),
                receipt.Request.Folder, StringComparison.OrdinalIgnoreCase)
            || (thread.TryGetProperty("historyMode", out var historyMode)
                && (historyMode.ValueKind != JsonValueKind.String || historyMode.GetString() != "legacy"))
            || !thread.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array
            || turns.GetArrayLength() > 500)
            throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
        JsonElement? exact = null;
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var turn in turns.EnumerateArray())
        {
            var id = RequiredString(turn, "id");
            if (!identities.Add(id)) throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
            if (id != receipt.TurnId) continue;
            if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
                || items.GetArrayLength() > 2048
                || (turn.TryGetProperty("itemsView", out var view)
                    && (view.ValueKind != JsonValueKind.String || view.GetString() != "full")))
                throw new InvalidOperationException(CodexAppServerConnection.IncompatibleMessage);
            if (RequiredString(turn, "status") is "completed" or "failed" or "interrupted") exact = turn.Clone();
        }
        return exact;
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
