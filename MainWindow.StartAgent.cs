using System.Windows;
using FullStackLauncher.Controls;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly Dictionary<string, StartAgentDraft> _startAgentDrafts = new(StringComparer.Ordinal);
    private CodexStartAgentWindow? _startAgentWindow;

    public bool CanStartAgent => SelectedProject is not null && !_closed && !_closing && !_closeRequested &&
        !_savingProjectEdits && _startAgentWindow is null;

    private bool HasStartAgentDrafts => _startAgentWindow is not null ||
        _startAgentDrafts.Values.Any(draft => !string.IsNullOrWhiteSpace(draft.Text) || draft.Images.Count > 0);

    private void StartCodexAgent_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartAgent || SelectedProject is not { } project) return;
        // Capture project identity before the dialog's nested dispatcher can observe other changes.
        var draft = _startAgentDrafts.GetValueOrDefault(project.Id);
        CodexStartAgentWindow? window = null;
        try
        {
            window = new CodexStartAgentWindow(project.Id, project.Name, _store.ResolveRoot(project),
                draft?.Text ?? "", draft?.PendingAttemptId, draft?.Images) { Owner = this };
            _startAgentWindow = window;
            Changed(nameof(CanStartAgent));
            window.ShowDialog();
            if (window.SentSuccessfully)
                Notice = $"New Codex chat started for {project.Name}.";
        }
        catch (Exception ex)
        {
            Notice = $"Start Agent could not open: {SensitiveDataProtection.Redact(ex.Message)}";
            MessageBox.Show(this, Notice, "Start Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (window is not null)
            {
                if (!string.IsNullOrEmpty(window.Draft) || window.DraftImages.Count > 0 || window.PendingAttemptId is not null)
                    _startAgentDrafts[project.Id] = new(window.Draft, window.PendingAttemptId, window.DraftImages);
                else _startAgentDrafts.Remove(project.Id);
            }
            _startAgentWindow = null;
            Changed(nameof(CanStartAgent));
        }
    }

    private bool PrepareStartAgentDraftsForClose()
    {
        if (_startAgentWindow is not null) return false;
        var count = _startAgentDrafts.Values.Count(draft => !string.IsNullOrWhiteSpace(draft.Text) || draft.Images.Count > 0);
        if (count == 0) return true;
        return MessageBox.Show(this,
            $"Discard {count} unsent Start Agent message{(count == 1 ? "" : "s")} and close Launcher? " +
            "Saved project context and submitted chats are kept.",
            "Unsent Codex messages", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private bool PrepareStartAgentDraftForRemoval(string projectId)
    {
        if (_startAgentWindow is not null) return false;
        if (!_startAgentDrafts.TryGetValue(projectId, out var draft) ||
            (string.IsNullOrWhiteSpace(draft.Text) && draft.Images.Count == 0)) return true;
        return MessageBox.Show(this,
            "Discard this project's unsent Start Agent message and remove the profile? " +
            "Saved context and submitted chats are kept.",
            "Unsent Codex message", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private sealed record StartAgentDraft(string Text, string? PendingAttemptId, IReadOnlyList<CodexAgentChatImage> Images);
}
