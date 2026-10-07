using System.Windows;
using System.Windows.Controls;

namespace FullStackLauncher.Controls;

public partial class CodexActivityStrip
{
    private readonly Dictionary<string, ReplyDraft> _replyDrafts = new(StringComparer.Ordinal);
    private CodexReplyWindow? _replyWindow;
    public bool HasReplyDrafts => _replyDrafts.Count > 0 || _replyWindow is not null;

    public bool PrepareReplyDraftsForClose()
    {
        if (_replyWindow is not null) return false;
        if (_replyDrafts.Count == 0) return true;
        var choice = MessageBox.Show(Window.GetWindow(this),
            $"Discard {_replyDrafts.Count} unsent Codex repl{(_replyDrafts.Count == 1 ? "y" : "ies")} and close Launcher?",
            "Unsent Codex replies", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes) return false;
        return true;
    }

    public void DiscardReplyDraftsAfterClose() => _replyDrafts.Clear();

    private void ReplyAgent_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: ActivityCard card } button || _replyWindow is not null) return;
        // Capture the exact root chat identity before activity refresh can change any displayed card.
        var threadId = card.Id;
        var title = card.Title;
        var agentMessage = card.ThoughtBubbleText;
        var draft = _replyDrafts.GetValueOrDefault(threadId) ?? new ReplyDraft("", Guid.NewGuid().ToString("D"), false);
        CloseMenus();
        HideThoughtPopups();
        var reply = new CodexReplyWindow(threadId, title, draft.Text, draft.ReplyId, draft.RetryLocked, agentMessage) { Owner = Window.GetWindow(this) };
        _replyWindow = reply;
        try
        {
            reply.ShowDialog();
            if (!string.IsNullOrEmpty(reply.Draft)) _replyDrafts[threadId] = new(reply.Draft, reply.ReplyId, reply.RetryLocked);
            else _replyDrafts.Remove(threadId);
        }
        finally
        {
            _replyWindow = null;
            if (button.IsLoaded && button.IsVisible) button.Focus();
        }
    }

    private sealed record ReplyDraft(string Text, string ReplyId, bool RetryLocked);
}
