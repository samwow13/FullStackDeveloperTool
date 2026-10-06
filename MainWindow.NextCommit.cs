using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FullStackLauncher.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private const string NextCommitLiveStatus = "Live preview · checks for AI updates every 3 seconds.";
    private readonly CancellationTokenSource _nextCommitLifetime = new();
    private bool _refreshingNextCommit;
    private bool _nextCommitRefreshRequested;
    private bool _updatingNextCommitRepositories;
    private bool _markingNextCommitViewed;
    private int _nextCommitProjectGeneration;
    private string _nextCommitMessage = "";
    private string _nextCommitStatus = "Choose a project to preview its next commit.";
    private string _nextCommitDraftHint = "";
    private int _nextCommitUnviewedCount;
    private NextCommitRepository? _selectedNextCommitRepository;
    private CommitMessagePresentation _nextCommitPresentation = new("", [], null);
    private readonly HashSet<(string Root, string Branch, string Remote, string Connection, string Entry)> _nextCommitAcknowledgedIds = [];
    private readonly Dictionary<string, (AgentGitSummaryBatch? Batch, string? Error)> _nextCommitBatches = [];
    private readonly Dictionary<string, string> _nextCommitRepositorySelections = [];

    public ObservableCollection<NextCommitRepository> NextCommitRepositories { get; } = [];
    public bool HasNextCommitRepositoryChoices => NextCommitRepositories.Count > 1;
    public NextCommitRepository? SelectedNextCommitRepository
    {
        get => _selectedNextCommitRepository;
        set
        {
            if (ReferenceEquals(_selectedNextCommitRepository, value)) return;
            _selectedNextCommitRepository = value;
            if (value is not null && SelectedProject is { } project && NextCommitRepositories.Contains(value))
                _nextCommitRepositorySelections[project.Id] = value.RepositoryId;
            Changed(nameof(SelectedNextCommitRepository));
            PresentNextCommit();
        }
    }
    public string NextCommitMessage => _nextCommitMessage;
    public CommitMessagePresentation NextCommitPresentation => _nextCommitPresentation;
    public string NextCommitStatus => _nextCommitStatus;
    public bool HasNextCommitStatusNotice => _nextCommitStatus != NextCommitLiveStatus;
    public string NextCommitDraftHint => _nextCommitDraftHint;
    public bool HasNextCommitDraftHint => _nextCommitDraftHint.Length > 0;
    public bool HasNextCommitMessage => _nextCommitMessage.Length > 0;
    public bool NextCommitOverLimit => _nextCommitMessage.Length > GitCommitMessage.MaximumLength;
    public string NextCommitCharacterCount => $"Commit message: {_nextCommitMessage.Length:N0} / {GitCommitMessage.MaximumLength:N0} characters";
    public string NextCommitLimitStatus => NextCommitOverLimit
        ? $"{_nextCommitMessage.Length - GitCommitMessage.MaximumLength:N0} characters over limit. Shorten the message in Git before committing."
        : $"{GitCommitMessage.MaximumLength - _nextCommitMessage.Length:N0} characters remaining.";
    public string GitCommitButtonLabel => _nextCommitUnviewedCount > 0 ? $"Git Commit · {_nextCommitUnviewedCount:N0} new" : "Git Commit";

    private void InitializeNextCommitSettings()
    {
        Deactivated += (_, _) => NextCommitSettingsToggle.IsChecked = false;
        LocationChanged += (_, _) => NextCommitSettingsToggle.IsChecked = false;
        SizeChanged += (_, _) => NextCommitSettingsToggle.IsChecked = false;
        NextCommitPanel.SizeChanged += (_, _) => NextCommitSettingsToggle.IsChecked = false;
        NextCommitPanel.IsVisibleChanged += (_, _) => NextCommitSettingsToggle.IsChecked = false;
        Closed += (_, _) => NextCommitSettingsToggle.IsChecked = false;
    }

    private void NextCommitSettingsMenu_Opened(object sender, EventArgs e) =>
        NextCommitSettingsMenu.Child?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));

    private void NextCommitSettingsToggle_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down && e.Key != Key.Enter) return;
        NextCommitSettingsToggle.IsChecked = true;
        e.Handled = true;
    }

    private void NextCommitSettingsMenu_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        NextCommitSettingsToggle.IsChecked = false;
        NextCommitSettingsToggle.Focus();
        e.Handled = true;
    }

    private async void GitCommit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is null || IsEditing || _closeRequested || _closing || _closed) return;
        NextCommitVisible = true;
        await RefreshNextCommitAsync(force: true);
    }

    private async void OpenGit_Click(object sender, RoutedEventArgs e) =>
        await OpenGitWorkspaceAsync(SelectedNextCommitRepository?.RepositoryRoot);

    private void CloseGitCommit_Click(object sender, RoutedEventArgs e)
    {
        NextCommitVisible = false;
        if (!GitCommitButton.Focus()) SectionsMenuToggle.Focus();
    }

    private void ResetNextCommitProject()
    {
        NextCommitSettingsToggle.IsChecked = false;
        _nextCommitProjectGeneration++;
        _dashboardGitScopePending = SelectedProject is { IsArchived: false };
        _nextCommitBatches.Clear();
        _nextCommitAcknowledgedIds.Clear();
        NextCommitRepositories.Clear();
        Changed(nameof(HasNextCommitRepositoryChoices));
        SelectedNextCommitRepository = null;
        _nextCommitUnviewedCount = 0;
        _nextCommitStatus = SelectedProject is null ? "Choose a project to preview its next commit." : "Reading pending AI updates…";
        PresentNextCommit();
        _ = RefreshNextCommitAsync();
    }

    private async Task RefreshNextCommitAsync(bool force = false)
    {
        if (_refreshingNextCommit) { _nextCommitRefreshRequested |= force; return; }
        if (_closeRequested || _closing || _closed || SelectedProject is not { } project) return;
        _nextCommitRefreshRequested = false;
        _refreshingNextCommit = true;
        var generation = _nextCommitProjectGeneration;
        try
        {
            var discovery = await DiscoverAgentGitAsync(project, null, _nextCommitLifetime.Token);
            var results = await Task.WhenAll(discovery.Repositories.Select(async repository =>
            {
                if (!repository.CanRecordChanges || repository.ActiveConnectionId is null || repository.Branch is null)
                    return (Repository: repository, Batch: (AgentGitSummaryBatch?)null,
                        Error: repository.Detail ?? "Choose an active connection and a named branch in Git.");
                var remote = repository.Connections.Single(connection => connection.IsActive);
                try
                {
                    var batch = await AgentGitChangeStore.ReadPendingAsync(repository.RepositoryRoot, repository.Branch,
                        remote.RemoteName, remote.ConnectionId, _nextCommitLifetime.Token);
                    return (Repository: repository, Batch: (AgentGitSummaryBatch?)batch, Error: (string?)null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    return (Repository: repository, Batch: (AgentGitSummaryBatch?)null,
                        Error: (string?)"Pending AI updates could not be read. Open Git to review repository access and the change ledger.");
                }
            }));
            if (_closeRequested || _closing || _closed || generation != _nextCommitProjectGeneration || !ReferenceEquals(project, SelectedProject)) return;
            EnsureAgentGitProjectCurrent(discovery);
            _dashboardGitScopePending = false;
            var repositoryNames = GitRepositoryDisplayNames.Create(_store.ResolveRoot(project),
                results.Select(result => result.Repository.RepositoryRoot));
            var oldId = SelectedNextCommitRepository?.RepositoryId
                ?? _nextCommitRepositorySelections.GetValueOrDefault(project.Id);
            _nextCommitBatches.Clear();
            foreach (var result in results)
                _nextCommitBatches[result.Repository.RepositoryId] = (ApplyNextCommitAcknowledgments(result.Batch), result.Error);
            var choices = results.Select(result => new NextCommitRepository(result.Repository.RepositoryId,
                result.Repository.RepositoryRoot, result.Repository.Branch, result.Repository.ActiveConnectionId,
                result.Batch?.RemoteName ?? result.Repository.Connections.FirstOrDefault(connection => connection.IsActive)?.RemoteName,
                repositoryNames[result.Repository.RepositoryRoot],
                _nextCommitBatches[result.Repository.RepositoryId].Batch?.Entries.Count)).ToArray();
            if (!NextCommitRepositories.SequenceEqual(choices))
            {
                // Rebuilding picker items may temporarily clear selection. Keep the
                // reminder countdown when the final selected scope is unchanged.
                _updatingNextCommitRepositories = true;
                try
                {
                    NextCommitRepositories.Clear();
                    foreach (var choice in choices) NextCommitRepositories.Add(choice);
                    SelectedNextCommitRepository = NextCommitRepositories.FirstOrDefault(choice => choice.RepositoryId == oldId)
                        ?? NextCommitRepositories.FirstOrDefault();
                }
                finally { _updatingNextCommitRepositories = false; }
                Changed(nameof(HasNextCommitRepositoryChoices));
            }
            UpdateNextCommitUnreadCount();
            _nextCommitStatus = discovery.Warning is not null
                ? "Some repository folders could not be checked. Refresh or open Git."
                : choices.Length == 0
                ? discovery.Folders.Any(folder => folder.State != nameof(GitBranchState.NotRepository))
                    ? "Git information is unavailable. Open Git to review the configured folders."
                    : "No Git repository in this project's configured folders. Open Git to set one up."
                : NextCommitLiveStatus;
            PresentNextCommit();
        }
        catch (OperationCanceledException) when (_nextCommitLifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (generation != _nextCommitProjectGeneration || !ReferenceEquals(project, SelectedProject) || _closeRequested || _closing || _closed) return;
            _dashboardGitScopePending = false;
            _nextCommitBatches.Clear();
            _nextCommitUnviewedCount = 0;
            _nextCommitStatus = "Preview unavailable. Refresh or open Git to review the configured folders.";
            PresentNextCommit();
        }
        finally
        {
            _refreshingNextCommit = false;
            if ((generation != _nextCommitProjectGeneration || _nextCommitRefreshRequested) && !_closeRequested && !_closing && !_closed) _ = RefreshNextCommitAsync();
        }
    }

    private void PresentNextCommit()
    {
        _nextCommitMessage = "";
        _nextCommitDraftHint = "";
        AgentGitSummaryBatch? batch = null;
        AgentGitSummaryBatch? displayedBatch = null;
        var includePlainLines = false;
        if (SelectedNextCommitRepository is { } selected && _nextCommitBatches.TryGetValue(selected.RepositoryId, out var result))
        {
            batch = result.Batch;
            if (batch is null) _nextCommitDraftHint = result.Error ?? "This repository's preview is unavailable.";
            else
            {
                var draft = GitCommitPreviewState.Read(batch.RepositoryRoot, batch.Branch, batch.ConnectionId);
                displayedBatch = batch;
                if (draft is { IsCustom: true } or { IsReviewOpen: true })
                {
                    displayedBatch = draft.IncludeAgentSummaries ? batch with
                    {
                        Entries = batch.Entries.Where(entry => draft.ReviewedEntryIds.Contains(entry.Id)).ToArray()
                    } : null;
                    includePlainLines = draft.IsCustom;
                }
                else if (draft is { InclusionWasChosen: true, IncludeAgentSummaries: false }) displayedBatch = null;
                _nextCommitMessage = draft is { IsCustom: true } or { IsReviewOpen: true } ? draft.Message
                    : draft is not null ? GitCommitMessage.Compose(draft.Title, !draft.InclusionWasChosen || draft.IncludeAgentSummaries ? batch : null)
                    : GitCommitMessage.ComposeUpdate(batch.Branch, batch);
                if (draft is { IsCustom: true } or { IsReviewOpen: true })
                {
                    var newer = batch.Entries.Count(entry => !draft.ReviewedEntryIds.Contains(entry.Id));
                    _nextCommitDraftHint = newer > 0
                        ? $"{(draft.IsCustom ? "Custom Git draft" : "Open Git review")} · {newer:N0} AI update(s) need review. Reopen commit review to include new updates."
                        : draft.IsCustom ? "Custom Git draft · edits kept for this app session." : "Exact message in the open Git review.";
                }
                else if (draft is { InclusionWasChosen: true, IncludeAgentSummaries: false })
                    _nextCommitDraftHint = "AI summaries excluded from this message. Review pending updates in Git.";
                else if (batch.Entries.Count == 0) _nextCommitDraftHint = "No pending AI updates. This prepared message is used only when a new commit is needed.";
            }
        }
        _nextCommitPresentation = CreateNextCommitPresentation(_nextCommitMessage, displayedBatch, includePlainLines);
        SynchronizeDashboardGitScope();
        UpdateNextCommitReminder();
        foreach (var name in new[] { nameof(NextCommitMessage), nameof(NextCommitPresentation), nameof(NextCommitStatus), nameof(HasNextCommitStatusNotice), nameof(NextCommitDraftHint), nameof(HasNextCommitDraftHint),
            nameof(HasNextCommitMessage), nameof(NextCommitOverLimit), nameof(NextCommitCharacterCount),
            nameof(NextCommitLimitStatus), nameof(GitCommitButtonLabel) }) Changed(name);
    }

    private async void RefreshNextCommit_Click(object sender, RoutedEventArgs e) => await RefreshNextCommitAsync(force: true);

    private void CopyNextCommit_Click(object sender, RoutedEventArgs e)
    {
        if (!HasNextCommitMessage) return;
        try { Clipboard.SetText(NextCommitMessage); Notice = "Copied the next commit message."; }
        catch (Exception) { Notice = "The commit message could not be copied. Try again."; }
    }

    private async void NextCommitMessage_ReadRequested(object? sender, CommitMessageReadEventArgs e)
    {
        if (_markingNextCommitViewed || _closeRequested || _closing || e.Presentation.DisplayedUnviewedBatch is not { Entries.Count: > 0 } captured
            || SelectedNextCommitRepository is not { } selected
            || !_nextCommitBatches.TryGetValue(selected.RepositoryId, out var result) || result.Batch is not { } current
            || !SameNextCommitScope(captured, current)) return;
        var generation = _nextCommitProjectGeneration;
        _markingNextCommitViewed = true;
        try
        {
            await AgentGitChangeStore.MarkViewedAsync(captured, _nextCommitLifetime.Token);
            if (generation == _nextCommitProjectGeneration)
            {
                foreach (var entry in captured.Entries) _nextCommitAcknowledgedIds.Add(NextCommitAcknowledgmentKey(captured, entry.Id));
                // Apply the acknowledgment only after its durable save, preserving later arrivals.
                if (_nextCommitBatches.TryGetValue(selected.RepositoryId, out var latest)
                    && latest.Batch is { } latestBatch && SameNextCommitScope(captured, latestBatch))
                    _nextCommitBatches[selected.RepositoryId] = (ApplyNextCommitAcknowledgments(latestBatch), latest.Error);
                UpdateNextCommitUnreadCount();
                PresentNextCommit();
            }
            await RefreshNextCommitAsync(force: true);
        }
        catch (OperationCanceledException) when (_nextCommitLifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (generation == _nextCommitProjectGeneration && SelectedNextCommitRepository is { } latestSelection
                && _nextCommitBatches.TryGetValue(latestSelection.RepositoryId, out var latest)
                && latest.Batch is { } latestBatch && SameNextCommitScope(captured, latestBatch)
                && !_closeRequested && !_closing && !_closed)
                Notice = "Read status could not be saved. Unread marks and pending AI updates were preserved; click the message to retry.";
        }
        finally { _markingNextCommitViewed = false; }
    }

    private void UpdateNextCommitUnreadCount() => _nextCommitUnviewedCount =
        _nextCommitBatches.Values.Sum(result => result.Batch?.Entries.Count(entry => entry.ViewedAt is null) ?? 0);

    private AgentGitSummaryBatch? ApplyNextCommitAcknowledgments(AgentGitSummaryBatch? batch) => batch is null ? null
        : batch with { Entries = batch.Entries.Select(entry => entry.ViewedAt is null && _nextCommitAcknowledgedIds.Contains(NextCommitAcknowledgmentKey(batch, entry.Id))
            ? entry with { ViewedAt = DateTimeOffset.UtcNow } : entry).ToArray() };

    private static (string, string, string, string, string) NextCommitAcknowledgmentKey(AgentGitSummaryBatch batch, string entryId) =>
        (GitConnectionService.NormalizeRoot(batch.RepositoryRoot).ToUpperInvariant(), batch.Branch, batch.RemoteName, batch.ConnectionId, entryId);

    private static bool SameNextCommitScope(AgentGitSummaryBatch left, AgentGitSummaryBatch right) =>
        string.Equals(left.RepositoryRoot, right.RepositoryRoot, StringComparison.OrdinalIgnoreCase)
        && left.Branch == right.Branch && left.RemoteName == right.RemoteName && left.ConnectionId == right.ConnectionId;

    private static CommitMessagePresentation CreateNextCommitPresentation(string message, AgentGitSummaryBatch? batch, bool includePlainLines)
    {
        if (batch is null || message.Length == 0) return new(message, [], null);
        var lines = new Dictionary<string, List<CommitMessageUnreadRange>>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        foreach (var line in message.Split('\n'))
        {
            var text = line.Trim();
            var hasBullet = text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal)
                || text.StartsWith("• ", StringComparison.Ordinal);
            if (hasBullet) text = text[2..];
            if (hasBullet || includePlainLines)
            {
                if (!lines.TryGetValue(text, out var matches)) lines[text] = matches = [];
                matches.Add(new(offset, line.TrimEnd('\r').Length));
            }
            offset += line.Length + 1;
        }
        // Partial or excluded reports remain unread: clicking this message cannot acknowledge text it does not show.
        var displayed = batch.Entries.Where(entry => entry.ViewedAt is null && entry.Bullets.All(lines.ContainsKey)).ToArray();
        var ranges = displayed.SelectMany(entry => entry.Bullets).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(bullet => lines[bullet]).Distinct().OrderBy(range => range.Start).ToArray();
        return new(message, ranges, displayed.Length == 0 ? null : batch with { Entries = displayed });
    }

    public sealed record NextCommitRepository(string RepositoryId, string RepositoryRoot, string? Branch, string? ConnectionId, string? RemoteName,
        string? Label = null, int? PendingUpdateCount = null)
    {
        public string DisplayName => $"{Label ?? System.IO.Path.GetFileName(RepositoryRoot)} · {Branch ?? "branch unavailable"}"
            + (PendingUpdateCount is > 0 ? $" · {PendingUpdateCount:N0} pending" : "");
    }

}
