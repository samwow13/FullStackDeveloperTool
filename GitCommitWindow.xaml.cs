using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public enum GitCommitMode
{
    CommitAndPush,
    PrepareSync,
    FinishSync
}

public partial class GitCommitWindow : Window, IDashboardReplacementWindow
{
    private readonly GitRepositorySnapshot _snapshot;
    private readonly GitRemoteInfo _remote;
    private readonly GitCommitMode _mode;
    private readonly bool _freezeDestination;
    private readonly string _fixedDestination;
    private readonly string _sourceLabel;
    private readonly AgentGitSummaryBatch? _agentSummaries;
    private readonly AgentGitSummaryBatch? _reviewedAgentSummaries;
    private readonly bool _canIncludeAgentSummaries;
    private readonly string? _previewConnectionId;
    private bool _ready;
    private bool _settingMessage;
    private bool _messageEdited;
    private readonly bool _includeAgentSummaries;
    private readonly bool _inclusionWasChosen;
    private string? _messagePreparationError;
    private readonly CancellationTokenSource _trackingCancellation = new();
    private GitCommitTracking? _tracking;
    private string? _trackingError;
    private bool _trackingLoading;
    private bool _closeAfterTracking;

    public GitCommitWindow(GitRepositorySnapshot snapshot, GitRemoteInfo remote, string? lastCreatedBranch,
        string? branchDraft = null, string? messageDraft = null,
        GitCommitMode mode = GitCommitMode.CommitAndPush, string? sourceLabel = null, string? recoveryNote = null,
        AgentGitSummaryBatch? agentSummaries = null, bool freezeDestination = false)
    {
        _snapshot = snapshot;
        _remote = remote;
        _mode = mode;
        _freezeDestination = freezeDestination;
        _fixedDestination = branchDraft?.Trim() ?? snapshot.Branch;
        _sourceLabel = string.IsNullOrWhiteSpace(sourceLabel) ? "the selected source" : sourceLabel;
        try { _previewConnectionId = AgentGitChangeStore.ConnectionId(remote); }
        catch (InvalidOperationException) { }
        var sharedDraft = mode != GitCommitMode.PrepareSync && snapshot.RepositoryRoot is { } root && _previewConnectionId is { } connectionId
            ? GitCommitPreviewState.Read(root, snapshot.Branch, connectionId) : null;
        var restoreSharedDraft = sharedDraft is { IsCustom: true }
            && (messageDraft == null || string.Equals(messageDraft, sharedDraft.Message, StringComparison.Ordinal));
        if (restoreSharedDraft) messageDraft = sharedDraft!.Message;
        _agentSummaries = agentSummaries is { Entries.Count: > 0 } ? agentSummaries : null;
        _reviewedAgentSummaries = restoreSharedDraft && _agentSummaries != null
            ? _agentSummaries with { Entries = _agentSummaries.Entries.Where(entry => sharedDraft!.ReviewedEntryIds.Contains(entry.Id, StringComparer.Ordinal)).ToArray() }
            : _agentSummaries;
        _canIncludeAgentSummaries = _agentSummaries != null && mode != GitCommitMode.PrepareSync
            && (snapshot.Changes.Count > 0 || snapshot.OperationState == "Merge in progress");
        _inclusionWasChosen = sharedDraft?.InclusionWasChosen == true;
        _includeAgentSummaries = restoreSharedDraft
            ? sharedDraft!.IncludeAgentSummaries && _reviewedAgentSummaries is { Entries.Count: > 0 }
            : _inclusionWasChosen ? sharedDraft!.IncludeAgentSummaries : messageDraft == null;
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);

        TargetBranchBox.Text = mode == GitCommitMode.PrepareSync ? snapshot.Branch : branchDraft ?? snapshot.Branch;
        _messageEdited = mode != GitCommitMode.PrepareSync && messageDraft != null;
        SetMessage(mode == GitCommitMode.PrepareSync ? CreateMessage() : messageDraft ?? CreateMessage());
        if (!string.IsNullOrWhiteSpace(recoveryNote))
        {
            RecoveryText.Visibility = Visibility.Visible;
            RecoveryText.Text = SensitiveDataProtection.Redact(recoveryNote);
        }
        ConfigureMode();
        if (freezeDestination && mode != GitCommitMode.PrepareSync)
        {
            DestinationEditor.Visibility = Visibility.Collapsed;
        }
        _ready = true;
        UpdatePreview();
        Loaded += async (_, _) =>
        {
            if (_mode == GitCommitMode.PrepareSync || _freezeDestination)
            {
                MessageBox.Focus();
                MessageBox.CaretIndex = 0;
            }
            else
            {
                TargetBranchBox.Focus();
                TargetBranchBox.SelectAll();
            }
            if (_mode != GitCommitMode.PrepareSync) await VerifyTrackingAsync();
        };
        Closing += (_, e) =>
        {
            if (!_trackingLoading) return;
            _closeAfterTracking = true;
            _trackingCancellation.Cancel();
            e.Cancel = true;
        };
        Closed += (_, _) =>
        {
            _trackingCancellation.Dispose();
            if (_mode != GitCommitMode.PrepareSync && _snapshot.RepositoryRoot is { } previewRoot && _previewConnectionId is { } previewConnection)
                GitCommitPreviewState.EndReview(previewRoot, _snapshot.Branch, previewConnection);
        };
    }

    public string TargetBranch => _mode == GitCommitMode.PrepareSync ? _snapshot.Branch
        : _freezeDestination ? _fixedDestination : TargetBranchBox.Text.Trim();
    public string Message => MessageBox.Text;
    public bool MessageWasEdited => _messageEdited;
    bool IDashboardReplacementWindow.IsDashboardReplacementReady => !_trackingLoading;
    bool IDashboardReplacementWindow.IsDashboardReplacementClosePending => _closeAfterTracking && _trackingLoading;
    public GitCommandExitUnconfirmedException? UnconfirmedCommand { get; private set; }
    public AgentGitSummaryBatch? ReviewedAgentSummaries => _canIncludeAgentSummaries && _includeAgentSummaries
        && _reviewedAgentSummaries is { Entries.Count: > 0 } ? _reviewedAgentSummaries : null;

    private void ConfigureMode()
    {
        var changedFiles = _snapshot.Changes.Count;
        switch (_mode)
        {
            case GitCommitMode.PrepareSync:
                Title = "Merge locally";
                Submit.Content = "_Merge locally";
                DestinationEditor.Visibility = Visibility.Collapsed;
                ActionDetails.Visibility = Visibility.Visible;
                ActionDetails.Text = changedFiles > 0
                    ? $"Fetch and verify {_sourceLabel}, then save all {changedFiles:N0} changed files in a local checkpoint before merging. Ignored files and submodule contents remain excluded. Review the merge before committing. Nothing is pushed."
                    : $"Fetch and verify {_sourceLabel}, then merge locally. No checkpoint is needed. Review the merge before committing. Nothing is pushed.";
                MessageBox.IsReadOnly = true;
                MessageLabel.Content = "Local checkpoint _message";
                break;
            case GitCommitMode.FinishSync:
                Title = "Commit all & push";
                Submit.Content = "Commit all & _push";
                break;
            default:
                Title = "Commit all & push";
                Submit.Content = "Commit all & _push";
                break;
        }
    }

    private string CreateTitle() => _mode switch
    {
        GitCommitMode.PrepareSync => $"Save local changes before merging {_sourceLabel} into {TargetBranch}",
        GitCommitMode.FinishSync => $"Merge {_sourceLabel} into {TargetBranch}",
        _ => $"Update {TargetBranch}"
    };

    private string CreateMessage() => GitCommitMessage.Compose(CreateTitle(),
        _canIncludeAgentSummaries && _includeAgentSummaries ? _reviewedAgentSummaries : null);

    private void SetMessage(string message)
    {
        _settingMessage = true;
        try { MessageBox.Text = message; }
        finally { _settingMessage = false; }
    }

    private async Task VerifyTrackingAsync()
    {
        _trackingLoading = true;
        try
        {
            _tracking = await GitRepositoryService.ReadCommitTrackingAsync(
                _snapshot.RepositoryRoot ?? _snapshot.Folder, _snapshot.Branch, _trackingCancellation.Token);
        }
        catch (GitCommandExitUnconfirmedException exception)
        {
            UnconfirmedCommand = exception;
            _trackingError = SensitiveDataProtection.Redact(exception.Message);
        }
        catch (OperationCanceledException) when (_closeAfterTracking) { }
        catch (Exception exception)
        {
            _trackingError = "Local branch tracking could not be verified. Close this review and refresh Git status.\n"
                + SensitiveDataProtection.Redact(exception.Message);
        }
        finally
        {
            _trackingLoading = false;
            UpdatePreview();
            if (_closeAfterTracking) Close();
        }
    }

    private void TargetBranch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _messagePreparationError = null;
        if (!_messageEdited)
        {
            try { SetMessage(CreateMessage()); }
            catch (InvalidOperationException exception) { _messagePreparationError = exception.Message; }
        }
        UpdatePreview();
    }

    private void Message_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _settingMessage) return;
        _messageEdited = true;
        _messagePreparationError = null;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var branch = TargetBranch;
        var error = ValidateDestination(branch) ?? _trackingError;
        Validation.Text = error ?? "";
        Validation.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        var current = string.Equals(branch, _snapshot.Branch, StringComparison.Ordinal);
        var destination = $"{_remote.Name}/{branch}";
        var upstream = current && _tracking?.UpstreamRemote is { } upstreamRemote && _tracking.UpstreamBranch is { } upstreamBranch
            ? upstreamRemote == "." ? upstreamBranch : $"{upstreamRemote}/{upstreamBranch}" : null;
        var tracksDestination = current && _tracking?.UpstreamRemote == _remote.Name && _tracking?.UpstreamBranch == branch;
        BranchRoute.Text = _mode == GitCommitMode.PrepareSync ? $"{_snapshot.Branch} (local merge)"
            : $"{branch}{(current ? "" : " (new)")} --> " + (_tracking == null
                ? $"{destination} ({(_trackingError == null ? "checking tracking" : "tracking unknown")})"
                : tracksDestination
                ? $"tracked {destination}"
                : string.IsNullOrWhiteSpace(upstream) ? $"untracked (push to {destination})"
                : $"{destination} (currently tracks {upstream})");
        BranchRoute.Text = SensitiveDataProtection.Redact(BranchRoute.Text);
        // A pending merge is finalized on its prepared branch before creating a new destination.
        ActionDetails.Visibility = _mode == GitCommitMode.PrepareSync || (_mode == GitCommitMode.FinishSync && !current)
            ? Visibility.Visible : Visibility.Collapsed;
        if (_mode == GitCommitMode.FinishSync && !current)
            ActionDetails.Text = "Commit the pending merge locally first, then create the new branch from that result and push.";
        var needsMessage = _mode != GitCommitMode.PrepareSync || _snapshot.Changes.Count > 0;
        var overLimit = Message.Length > GitCommitMessage.MaximumLength;
        MessageLength.Text = $"{Message.Length:N0} / {GitCommitMessage.MaximumLength:N0} characters"
            + (overLimit ? $" · {Message.Length - GitCommitMessage.MaximumLength:N0} over limit" : "");
        MessageLength.Foreground = overLimit ? new SolidColorBrush(Color.FromRgb(255, 172, 169)) : (Brush)FindResource("MutedBrush");
        var messageValid = _messagePreparationError == null && (!needsMessage || (!string.IsNullOrWhiteSpace(Message) && !overLimit && !Message.Contains('\0')));
        MessageValidation.Text = _messagePreparationError ?? (messageValid ? ""
            : overLimit ? "Shorten the commit message to at most 4,000 characters. All text is preserved for editing."
            : Message.Contains('\0') ? "Remove the null character from the commit message."
            : "Enter a commit message or keep the prepared message.");
        MessageValidation.Visibility = messageValid ? Visibility.Collapsed : Visibility.Visible;
        ReviewDetails.Visibility = RecoveryText.Visibility == Visibility.Visible || ActionDetails.Visibility == Visibility.Visible
            || Validation.Visibility == Visibility.Visible || MessageValidation.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        Submit.IsEnabled = error == null && messageValid && (_mode == GitCommitMode.PrepareSync || _tracking != null);
        if (_ready && _mode != GitCommitMode.PrepareSync && _snapshot.RepositoryRoot is { } root && _previewConnectionId is { } connectionId)
            GitCommitPreviewState.Publish(root, _snapshot.Branch, connectionId, Message, CreateTitle(), _messageEdited,
                _messageEdited ? ReviewedAgentSummaries != null : _includeAgentSummaries,
                ReviewedAgentSummaries?.Entries.Select(entry => entry.Id).ToArray() ?? [], _inclusionWasChosen);
    }

    private string? ValidateDestination(string branch)
    {
        if (_snapshot.IsDetached || string.IsNullOrWhiteSpace(_snapshot.Branch))
            return "Select a local branch in the Git workspace before continuing.";
        if (string.IsNullOrWhiteSpace(branch)) return _freezeDestination
            ? "Close this review and enter the current branch or a new branch in the Git workspace."
            : "Enter the current branch name or a new branch name.";
        if (branch.Length > 250 || branch is "HEAD" or "@" || branch.StartsWith('-') || branch.StartsWith('@') || branch.StartsWith('/') || branch.EndsWith('/')
            || branch.EndsWith('.') || branch.Contains("..", StringComparison.Ordinal)
            || branch.Contains("@{", StringComparison.Ordinal) || branch.Contains("//", StringComparison.Ordinal)
            || branch.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || "~^:?*[\\".Contains(character))
            || branch.Split('/').Any(part => part.StartsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal)))
            return "Enter a valid Git branch name without spaces or special revision characters.";
        if (!string.Equals(branch, _snapshot.Branch, StringComparison.Ordinal)
            && _snapshot.Branches.Any(item => !item.IsRemote && string.Equals(item.Name, branch, StringComparison.OrdinalIgnoreCase)))
            return "This local branch already exists. Use the current branch or choose a new branch name.";
        return null;
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        UpdatePreview();
        if (Submit.IsEnabled) DialogResult = true;
    }
}
