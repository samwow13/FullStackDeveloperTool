using System.IO;
using System.Windows;
using FullStackLauncher.Controls;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly CodexAgentWaitingStore _waitingAgentStore = new();
    private IReadOnlyList<CodexAgentWaitingItem> _waitingAgents = [];
    private bool _loadingWaitingAgents;
    private bool _waitingAgentRefreshRequested;
    private bool _releasingWaitingAgents;
    private string? _waitingAgentError;
    private CodexWaitingAgentsWindow? _waitingAgentsWindow;

    public int WaitingAgentCount => _waitingAgents.Count(item => item.IsWaiting);
    public bool HasWaitingAgentItems => _waitingAgents.Count > 0 || _waitingAgentError is not null || _releasingWaitingAgents;
    public string WaitingAgentLabel => _waitingAgentError is not null ? "Waiting prompts need attention"
        : _releasingWaitingAgents ? "Starting waiting prompts…"
        : $"Waiting prompts ({WaitingAgentCount})" +
          (_waitingAgents.Count > WaitingAgentCount ? $" · {_waitingAgents.Count - WaitingAgentCount} to review" : "");
    public string WaitingAgentGoLabel => _releasingWaitingAgents ? "Starting…" : $"Go ({WaitingAgentCount})";
    public bool CanReleaseWaitingAgents => WaitingAgentCount > 0 && _waitingAgentError is null &&
        !_loadingWaitingAgents && !_releasingWaitingAgents && !_closed && !_closing && !_closeRequested &&
        !_savingProjectEdits && _startAgentWindow is null && _waitingAgentsWindow is null;
    public bool CanReviewWaitingAgents => !_closed && !_closing && !_closeRequested &&
        !_releasingWaitingAgents && _startAgentWindow is null && _waitingAgentsWindow is null;

    private void InitializeWaitingAgents()
    {
        Loaded += async (_, _) => await RefreshWaitingAgentsAsync();
        Activated += async (_, _) => await RefreshWaitingAgentsAsync();
    }

    private async Task RefreshWaitingAgentsAsync()
    {
        if (_closed) return;
        if (_loadingWaitingAgents)
        {
            _waitingAgentRefreshRequested = true;
            return;
        }
        _loadingWaitingAgents = true;
        UpdateWaitingAgentActions();
        try
        {
            var items = await Task.Run(() => _waitingAgentStore.List());
            if (_closed) return;
            _waitingAgents = items;
            _waitingAgentError = null;
        }
        catch (Exception ex)
        {
            if (_closed) return;
            _waitingAgentError = SensitiveDataProtection.Redact(ex.Message);
            Notice = "Waiting prompts could not be read. Saved prompts are kept. " + _waitingAgentError;
        }
        finally
        {
            _loadingWaitingAgents = false;
            if (!_closed) UpdateWaitingAgentActions();
            if (_waitingAgentRefreshRequested && !_closed)
            {
                _waitingAgentRefreshRequested = false;
                _ = RefreshWaitingAgentsAsync();
            }
        }
    }

    private async void StartAgentWaitingSaved(object? sender, EventArgs e) => await RefreshWaitingAgentsAsync();

    private void UpdateWaitingAgentActions()
    {
        foreach (var name in new[] { nameof(WaitingAgentCount), nameof(HasWaitingAgentItems), nameof(WaitingAgentLabel),
                     nameof(WaitingAgentGoLabel), nameof(CanReleaseWaitingAgents), nameof(CanReviewWaitingAgents), nameof(CanStartAgent) })
            Changed(name);
    }

    private async void GoWaitingAgents_Click(object sender, RoutedEventArgs e)
    {
        if (!CanReleaseWaitingAgents) return;
        // Freeze the configured roots before yielding, just as Start Agent captures its project.
        _releasingWaitingAgents = true;
        UpdateWaitingAgentActions();
        try
        {
            var roots = Projects.ToDictionary(project => project.Id, project => _store.ResolveRoot(project), StringComparer.Ordinal);
            var batch = (await Task.Run(() => _waitingAgentStore.List())).Where(item => item.IsWaiting).ToArray();
            foreach (var item in batch)
            {
                if (!roots.TryGetValue(item.Request.ProjectId, out var root) ||
                    !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Request.Folder)), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"The saved project folder for {item.Request.ProjectName} changed or was removed. Review its waiting prompt before starting.");
            }
            if (batch.Length == 0) return;
            Notice = $"Starting {batch.Length} waiting prompt(s)…";
            // Start all independent chat owners together; do not await one turn before releasing another.
            var results = await Task.WhenAll(batch.Select(async item =>
            {
                try { return (Receipt: await _waitingAgentStore.ReleaseAsync(item.Id), Error: (string?)null); }
                catch (Exception ex) { return (Receipt: (CodexAgentChatReceipt?)null,
                    Error: $"{item.Request.ProjectName}: {SensitiveDataProtection.Redact(ex.Message)}"); }
            }));
            var accepted = results.Count(result => result.Receipt?.IsAccepted == true);
            var errors = results.Where(result => result.Error is not null).Select(result => result.Error);
            var unresolved = results.Length - accepted;
            Notice = $"Started {accepted} of {batch.Length} Codex chats." +
                (unresolved > 0 ? $" {unresolved} submission(s) need review; saved attempts are kept." : "") +
                (errors.Any() ? " " + string.Join(" ", errors) : "");
        }
        catch (Exception ex)
        {
            Notice = "Waiting prompts could not start. Saved prompts are kept. " + SensitiveDataProtection.Redact(ex.Message);
            MessageBox.Show(this, Notice, "Waiting prompts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _releasingWaitingAgents = false;
            await RefreshWaitingAgentsAsync();
            UpdateWaitingAgentActions();
        }
    }

    private async void ReviewWaitingAgents_Click(object sender, RoutedEventArgs e)
    {
        if (!CanReviewWaitingAgents) return;
        try
        {
            var window = new CodexWaitingAgentsWindow { Owner = this };
            _waitingAgentsWindow = window;
            window.SubmissionReviewRequested += ReviewWaitingAgentSubmission;
            UpdateWaitingAgentActions();
            window.ShowDialog();
            window.SubmissionReviewRequested -= ReviewWaitingAgentSubmission;
        }
        catch (Exception ex)
        {
            Notice = "Waiting prompts could not open. " + SensitiveDataProtection.Redact(ex.Message);
            MessageBox.Show(this, Notice, "Waiting prompts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _waitingAgentsWindow = null;
            await RefreshWaitingAgentsAsync();
            UpdateWaitingAgentActions();
        }
    }

    private void ReviewWaitingAgentSubmission(CodexAgentWaitingItem item)
    {
        if (_startAgentWindow is not null || item.AttemptId is null) return;
        var draft = _startAgentDrafts.GetValueOrDefault(item.Request.ProjectId);
        var window = new CodexStartAgentWindow(item.Request.ProjectId, item.Request.ProjectName,
            item.Request.Folder, draft?.Text ?? "", item.AttemptId, draft?.Images, draft?.KeepAwake ?? false,
            inspectSubmission: true) { Owner = _waitingAgentsWindow ?? (Window)this };
        _startAgentWindow = window;
        UpdateWaitingAgentActions();
        try { window.ShowDialog(); }
        finally
        {
            // Keep only the returned unsent draft; a saved or sent message must not reappear.
            if (!string.IsNullOrEmpty(window.Draft) || window.UnsentDraftImages.Count > 0 ||
                window.RecoveryAttemptId is not null || (window.DraftKeepAwake && !window.SentSuccessfully))
                _startAgentDrafts[item.Request.ProjectId] = new(window.Draft, window.RecoveryAttemptId,
                    window.UnsentDraftImages, window.DraftKeepAwake);
            else _startAgentDrafts.Remove(item.Request.ProjectId);
            _startAgentWindow = null;
            UpdateWaitingAgentActions();
        }
    }
}
