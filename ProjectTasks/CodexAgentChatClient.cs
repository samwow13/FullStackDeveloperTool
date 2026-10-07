using System.Diagnostics;
using System.IO;
using FullStackLauncher.CodexMonitor;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Starts a separate owner for each explicit chat; never enables or edits a queue.</summary>
public sealed class CodexAgentChatClient
{
    public const int MaximumPromptCharacters = 100_000;
    private readonly CodexAgentChatStore _store = new();

    public async Task<CodexAgentChatReceipt> StartAsync(CodexAgentChatRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var receipt = await Task.Run(() => _store.Prepare(request), cancellationToken).ConfigureAwait(false);
        // Once preparation succeeds, always return its identity even if launch or
        // observation fails. The caller must not lose an uncertain submission.
        try
        {
            var args = new List<string> { "--codex-agent-chat", receipt.AttemptId };
            if (CodexAgentStorage.SettingsOverride() is { } settings)
                args.AddRange(["--settings", settings]);
            var start = await Task.Run(() => MonitorRuntime.CreateStart(args.ToArray()), cancellationToken).ConfigureAwait(false);
            start.WindowStyle = ProcessWindowStyle.Hidden;
            using var process = Process.Start(start)
                ?? throw new IOException("The agent chat owner could not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(110));
            while (true)
            {
                receipt = _store.Read(receipt.AttemptId);
                if (receipt.IsAccepted || receipt.RequiresReview || receipt.State == CodexAgentChatState.Failed)
                    return receipt;
                if (process.HasExited)
                {
                    receipt = _store.Read(receipt.AttemptId);
                    return receipt.IsAccepted || receipt.RequiresReview || receipt.State == CodexAgentChatState.Failed
                        ? receipt
                        : Unconfirmed(receipt, "The agent chat owner exited before confirming submission. Review recent tasks in Codex before sending again.");
                }
                await Task.Delay(180, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            try { receipt = _store.Read(receipt.AttemptId); }
            catch (Exception) { }
            if (receipt.IsAccepted || receipt.RequiresReview || receipt.State == CodexAgentChatState.Failed) return receipt;
            return Unconfirmed(receipt, "Submission could not be confirmed. The agent may still start. Check status or review recent tasks in Codex before sending again.");
        }
    }

    public CodexAgentChatReceipt ReadReceipt(string attemptId)
    {
        var receipt = _store.Read(attemptId);
        if (receipt.State is CodexAgentChatState.Prepared or CodexAgentChatState.Starting or CodexAgentChatState.Running)
        {
            // The owner holds this handle for its entire lifetime. Its absence
            // means the saved running state is no longer fresh evidence.
            FileStream claim;
            try { claim = _store.Claim(attemptId); }
            catch (InvalidOperationException) { return receipt; }
            using (claim)
            {
                // The owner may have completed between the first read and lock acquisition.
                receipt = _store.Read(attemptId);
                if (receipt.TerminalConfirmed || receipt.State == CodexAgentChatState.Failed) return receipt;
                if (!receipt.SubmissionAttempted && receipt.ThreadId is not null)
                    return receipt with { State = CodexAgentChatState.Failed, Summary = "The chat was created, but no prompt was sent. Start a fresh chat when ready." };
                return Unconfirmed(receipt, "The agent chat owner is not connected. Review recent tasks in Codex before sending again.");
            }
        }
        return receipt;
    }

    public CodexAgentChatReceipt? FindPendingReceipt(string projectId)
    {
        CodexAgentStorage.RequireText(projectId, 200, "Select a project before checking agent chats.");
        if (!Directory.Exists(_store.DirectoryPath)) return null;
        // Only launcher-owned receipt filenames are examined; never Codex state.
        foreach (var file in new DirectoryInfo(_store.DirectoryPath).EnumerateFiles("*.json")
                     .OrderByDescending(file => file.LastWriteTimeUtc))
        {
            var attempt = Path.GetFileNameWithoutExtension(file.Name);
            if (!Guid.TryParseExact(attempt, "N", out _)) continue;
            if (_store.IsReviewed(attempt)) continue;
            var receipt = ReadReceipt(attempt);
            if (receipt.Request.ProjectId == projectId
                && receipt.State is CodexAgentChatState.Prepared or CodexAgentChatState.Starting or CodexAgentChatState.Unconfirmed)
                return receipt;
        }
        return null;
    }

    /// <summary>Called only after the user explicitly reviews the uncertain attempt. Never resends work.</summary>
    public void AcknowledgeReview(string attemptId)
    {
        using var claim = _store.Claim(attemptId);
        var receipt = _store.Read(attemptId);
        if (receipt.TerminalConfirmed || receipt.State == CodexAgentChatState.Failed)
            return;
        _store.RecordReview(attemptId);
    }

    private static CodexAgentChatReceipt Unconfirmed(CodexAgentChatReceipt receipt, string summary) =>
        receipt with { State = CodexAgentChatState.Unconfirmed, TerminalConfirmed = false, Summary = summary };
}
