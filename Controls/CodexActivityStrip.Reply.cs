using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

public partial class CodexActivityStrip
{
    private readonly Dictionary<string, ReplyDraft> _replyDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ThreadId, ReplyDraft Draft)> _recoveredReplyDrafts = new(StringComparer.Ordinal);
    private CodexReplyWindow? _replyWindow;
    private ReplyDraft? _openReplySourceDraft;
    private bool _replyReplacementCheckpointPrepared;
    private DashboardResumeStore? _replyResumeStore;
    private string? _replacementOpenReplyThreadId;
    private string? _replacementOpenReplyId;
    private string? _replyToResumeAfterReplacement;
    private string? _replyIdToResumeAfterReplacement;
    public const string ReplyReplacementResumeSection = "crew-reply-drafts";
    private int ReplyDraftCount => _replyDrafts.Count + _recoveredReplyDrafts.Count;
    public bool HasReplyDrafts => ReplyDraftCount > 0 || _replyWindow is not null;
    public bool HasSavedReplyDrafts => ReplyDraftCount > 0;

    internal void SaveReplyReplacementResumeState(DashboardResumeStore store, bool prepareReplacement = true)
    {
        _replyResumeStore = store;
        if (_replyWindow is { IsDashboardReplacementReady: false })
            throw new InvalidOperationException("Wait for the current reply save to finish before replacing Launcher.");
        var drafts = _replyDrafts.Select(pair => (ThreadId: pair.Key, Draft: pair.Value))
            .Concat(_recoveredReplyDrafts.Values).ToList();
        if (_replyWindow is { } window)
        {
            drafts.RemoveAll(pair => pair.ThreadId == window.ThreadId && pair.Draft.ReplyId == _openReplySourceDraft?.ReplyId);
            if (window.Draft.Length > 0)
                drafts.Add((window.ThreadId, CaptureReplyDraft(window, _openReplySourceDraft?.ProjectId ?? _selectedProjectId)));
        }
        store.Save(ReplyReplacementResumeSection, new CrewReplyResumeState
        {
            OpenThreadId = _replyWindow?.ThreadId ?? (prepareReplacement && _replyReplacementCheckpointPrepared ? _replacementOpenReplyThreadId : null),
            OpenReplyId = _replyWindow?.ReplyId ?? (prepareReplacement && _replyReplacementCheckpointPrepared ? _replacementOpenReplyId : null),
            Drafts = drafts.Select(pair => new CrewReplyDraftResumeState
            {
                ThreadId = pair.ThreadId, ProjectId = pair.Draft.ProjectId, Title = pair.Draft.Title,
                AgentMessage = pair.Draft.AgentMessage, Text = pair.Draft.Text,
                ReplyId = pair.Draft.ReplyId, RetryLocked = pair.Draft.RetryLocked,
                SelectionStart = pair.Draft.SelectionStart, SelectionLength = pair.Draft.SelectionLength,
                VerticalOffset = pair.Draft.VerticalOffset
            }).ToList()
        });
        if (prepareReplacement) _replyReplacementCheckpointPrepared = true;
        if (prepareReplacement && _replyWindow != null)
        {
            _replacementOpenReplyThreadId = _replyWindow.ThreadId;
            _replacementOpenReplyId = _replyWindow.ReplyId;
        }
    }

    internal void RestoreReplyReplacementResumeState(DashboardResumeStore store)
    {
        _replyResumeStore = store;
        var state = store.Load<CrewReplyResumeState>(ReplyReplacementResumeSection);
        if (state == null) return;
        if (state.Version != 1 || state.Drafts == null || state.Drafts.Any(draft => draft == null ||
                string.IsNullOrWhiteSpace(draft.ThreadId) || !Guid.TryParse(draft.ReplyId, out _) ||
                draft.Text == null || draft.Title == null || draft.AgentMessage == null))
            throw new InvalidOperationException("Saved Codex reply drafts cannot be restored. Their checkpoint is retained for recovery.");
        foreach (var saved in state.Drafts)
        {
            var draft = new ReplyDraft(saved.Text, saved.ReplyId, saved.RetryLocked,
                saved.ProjectId, saved.Title, saved.AgentMessage, saved.SelectionStart, saved.SelectionLength, saved.VerticalOffset);
            if (_replyDrafts.TryAdd(saved.ThreadId, draft)) continue;
            var current = _replyDrafts[saved.ThreadId];
            if (current.ReplyId == draft.ReplyId && current.Text == draft.Text)
            {
                _replyDrafts[saved.ThreadId] = current with { RetryLocked = current.RetryLocked || draft.RetryLocked };
                continue;
            }
            if (current.ReplyId == draft.ReplyId)
                throw new InvalidOperationException("Saved reply identity conflicts with a current draft. Both drafts and the checkpoint are retained; resolve the current reply before restoring.");
            if (_recoveredReplyDrafts.TryGetValue(draft.ReplyId, out var existing))
            {
                if (existing.ThreadId != saved.ThreadId || existing.Draft.Text != draft.Text)
                    throw new InvalidOperationException("Saved reply identity conflicts with another draft. Existing drafts and the checkpoint are retained for recovery.");
                _recoveredReplyDrafts[draft.ReplyId] = (existing.ThreadId,
                    existing.Draft with { RetryLocked = existing.Draft.RetryLocked || draft.RetryLocked });
            }
            else _recoveredReplyDrafts.Add(draft.ReplyId, (saved.ThreadId, draft));
        }
        _replyToResumeAfterReplacement = state.OpenThreadId;
        _replyIdToResumeAfterReplacement = state.OpenReplyId;
        NotifyView();
    }

    public void ResumeReplyWindowAfterReplacement()
    {
        var threadId = _replyToResumeAfterReplacement;
        if (threadId == null || _replyWindow != null) return;
        var draft = _replyDrafts.GetValueOrDefault(threadId);
        if (_replyIdToResumeAfterReplacement is { } replyId && draft?.ReplyId != replyId)
            draft = _recoveredReplyDrafts.TryGetValue(replyId, out var recovered) && recovered.ThreadId == threadId
                ? recovered.Draft : null;
        if (draft == null) return;
        _replyToResumeAfterReplacement = null;
        _replyIdToResumeAfterReplacement = null;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_replyWindow == null) OpenReplyWindow(threadId, draft.Title, draft.AgentMessage, draft);
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    public void CancelReplyReplacementPreparation()
    {
        _replyReplacementCheckpointPrepared = false;
        _replacementOpenReplyThreadId = null;
        _replacementOpenReplyId = null;
    }

    public bool PrepareReplyDraftsForClose(bool automaticReplacement = false)
    {
        if (automaticReplacement && _replyReplacementCheckpointPrepared)
            return _replyWindow == null;
        if (_replyWindow is not null) return false;
        if (ReplyDraftCount == 0) return ClearReplyResumeCheckpoint();
        var choice = MessageBox.Show(Window.GetWindow(this),
            $"Discard {ReplyDraftCount} unsent Codex repl{(ReplyDraftCount == 1 ? "y" : "ies")} and close Launcher?",
            "Unsent Codex replies", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes) return false;
        return ClearReplyResumeCheckpoint();
    }

    private bool ClearReplyResumeCheckpoint()
    {
        if (_replyResumeStore == null) return true;
        try { _replyResumeStore.Remove(ReplyReplacementResumeSection); return true; }
        catch (System.IO.IOException)
        {
            MessageBox.Show(Window.GetWindow(this),
                "Saved reply checkpoints could not be cleared. Existing replies are kept; check local storage access and close again.",
                "Close incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    public void DiscardReplyDraftsAfterClose()
    {
        _replyDrafts.Clear();
        _recoveredReplyDrafts.Clear();
        NotifyView();
    }

    private void ReplyDraftsMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu) return;
        menu.Items.Clear();
        foreach (var pair in _replyDrafts.Select(pair => (ThreadId: pair.Key, Draft: pair.Value, Recovered: false))
            .Concat(_recoveredReplyDrafts.Values.Select(pair => (pair.ThreadId, pair.Draft, Recovered: true))))
        {
            var threadId = pair.ThreadId;
            var draft = pair.Draft;
            var title = string.IsNullOrWhiteSpace(draft.Title) ? "Saved reply" : draft.Title;
            if (pair.Recovered) title += " · Recovered reply";
            var item = new MenuItem
            {
                Header = title.Length > 80 ? title[..80] + "…" : title,
                Style = (Style)FindResource("CrewViewMenuItem"),
                ToolTip = $"{title}\nChat ID: {threadId}" + (draft.ProjectId == null ? "" : $"\nProject ID: {draft.ProjectId}")
            };
            item.Click += (_, _) =>
            {
                if (_replyWindow == null)
                    OpenReplyWindow(threadId, draft.Title, draft.AgentMessage, draft);
            };
            menu.Items.Add(item);
        }
    }

    private void ReplyAgent_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: ActivityCard card } button || _replyWindow is not null) return;
        // Capture the exact root chat identity before activity refresh can change any displayed card.
        var threadId = card.Id;
        var title = card.Title;
        var agentMessage = card.ThoughtBubbleText;
        var draft = _replyDrafts.GetValueOrDefault(threadId) ?? new ReplyDraft("", Guid.NewGuid().ToString("D"), false,
            _selectedProjectId, title, agentMessage, 0, 0, 0);
        OpenReplyWindow(threadId, title, agentMessage, draft, button);
    }

    private void OpenReplyWindow(string threadId, string title, string agentMessage, ReplyDraft draft, Button? button = null)
    {
        CloseMenus();
        HideThoughtPopups();
        var reply = new CodexReplyWindow(threadId, title, draft.Text, draft.ReplyId, draft.RetryLocked, agentMessage) { Owner = Window.GetWindow(this) };
        _replyWindow = reply;
        _openReplySourceDraft = draft;
        try
        {
            reply.RestoreEditorState(draft.SelectionStart, draft.SelectionLength, draft.VerticalOffset);
            reply.ShowDialog();
            if (_recoveredReplyDrafts.Remove(draft.ReplyId))
            {
                if (reply.Draft.Length > 0)
                    _recoveredReplyDrafts[reply.ReplyId] = (threadId, CaptureReplyDraft(reply, draft.ProjectId));
            }
            else
            {
                if (!string.IsNullOrEmpty(reply.Draft)) _replyDrafts[threadId] = CaptureReplyDraft(reply, draft.ProjectId);
                else _replyDrafts.Remove(threadId);
            }
        }
        finally
        {
            _replyWindow = null;
            _openReplySourceDraft = null;
            if (_replyResumeStore != null && !_replyReplacementCheckpointPrepared)
            {
                try { SaveReplyReplacementResumeState(_replyResumeStore, prepareReplacement: false); }
                catch (System.IO.IOException)
                {
                    MessageBox.Show(Window.GetWindow(this),
                        "Reply checkpoint could not be updated. Current drafts and saved files are kept; check local storage access.",
                        "Reply autosave failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            NotifyView();
            if (button?.IsLoaded == true && button.IsVisible) button.Focus();
        }
    }

    private static ReplyDraft CaptureReplyDraft(CodexReplyWindow window, string? projectId) =>
        new(window.Draft, window.ReplyId, window.RetryLocked, projectId, window.ChatDisplayTitle,
            window.AgentMessageText, window.DraftSelectionStart, window.DraftSelectionLength, window.DraftVerticalOffset);

    private sealed record ReplyDraft(string Text, string ReplyId, bool RetryLocked,
        string? ProjectId, string Title, string AgentMessage, int SelectionStart, int SelectionLength, double VerticalOffset);
}

public sealed record CrewReplyResumeState
{
    public int Version { get; init; } = 1;
    public string? OpenThreadId { get; init; }
    public string? OpenReplyId { get; init; }
    public List<CrewReplyDraftResumeState> Drafts { get; init; } = [];
}

public sealed record CrewReplyDraftResumeState
{
    public string ThreadId { get; init; } = "";
    public string? ProjectId { get; init; }
    public string Title { get; init; } = "";
    public string AgentMessage { get; init; } = "";
    public string Text { get; init; } = "";
    public string ReplyId { get; init; } = "";
    public bool RetryLocked { get; init; }
    public int SelectionStart { get; init; }
    public int SelectionLength { get; init; }
    public double VerticalOffset { get; init; }
}
