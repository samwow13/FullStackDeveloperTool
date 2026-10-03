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

public partial class GitCommitWindow : Window
{
    private readonly GitRepositorySnapshot _snapshot;
    private readonly GitRemoteInfo _remote;
    private readonly string? _lastCreatedBranch;
    private readonly GitCommitMode _mode;
    private readonly bool _freezeDestination;
    private readonly string _fixedDestination;
    private readonly string _sourceLabel;
    private readonly AgentGitSummaryBatch? _agentSummaries;
    private AgentGitSummaryBatch? _reviewedAgentSummaries;
    private readonly bool _canIncludeAgentSummaries;
    private readonly string? _previewConnectionId;
    private bool _ready;
    private bool _settingMessage;
    private bool _messageEdited;
    private bool _includeAgentSummaries;
    private bool _inclusionWasChosen;
    private string? _messagePreparationError;

    public GitCommitWindow(GitRepositorySnapshot snapshot, GitRemoteInfo remote, string? lastCreatedBranch,
        string? branchDraft = null, string? messageDraft = null,
        GitCommitMode mode = GitCommitMode.CommitAndPush, string? sourceLabel = null, string? recoveryNote = null,
        AgentGitSummaryBatch? agentSummaries = null, bool freezeDestination = false)
    {
        _snapshot = snapshot;
        _remote = remote;
        _lastCreatedBranch = string.IsNullOrWhiteSpace(lastCreatedBranch) ? null : lastCreatedBranch;
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

        CurrentBranch.Text = snapshot.IsDetached ? "Detached HEAD" : snapshot.Branch;
        LastCreatedBranch.Text = _lastCreatedBranch ?? "Creation history unavailable";
        UseLastCreated.Visibility = _lastCreatedBranch == null || freezeDestination ? Visibility.Collapsed : Visibility.Visible;
        Repository.Text = snapshot.RepositoryRoot ?? snapshot.Folder;
        TargetBranchBox.Text = mode == GitCommitMode.PrepareSync ? snapshot.Branch : branchDraft ?? snapshot.Branch;
        _messageEdited = mode != GitCommitMode.PrepareSync && messageDraft != null;
        SetMessage(mode == GitCommitMode.PrepareSync ? CreateMessage() : messageDraft ?? CreateMessage());
        if (!string.IsNullOrWhiteSpace(recoveryNote))
        {
            RecoveryNotice.Visibility = Visibility.Visible;
            RecoveryText.Text = SensitiveDataProtection.Redact(recoveryNote);
        }
        ConfigureMode();
        if (freezeDestination && mode != GitCommitMode.PrepareSync)
        {
            DestinationEditor.Visibility = Visibility.Collapsed;
            DestinationReview.Visibility = Visibility.Visible;
            TargetBranchReview.Text = _fixedDestination;
        }
        if (_agentSummaries != null && mode != GitCommitMode.PrepareSync)
        {
            AgentSummaryNotice.Visibility = Visibility.Visible;
            var unviewed = _agentSummaries.Entries.Count(entry => entry.ViewedAt == null);
            AgentSummaryInfo.Text = $"{_agentSummaries.Entries.Count:N0} pending agent update(s) · {unviewed:N0} unviewed · {_agentSummaries.Entries.Count - unviewed:N0} viewed\n{snapshot.Branch} · {remote.Name}";
            AgentSummaryText.Text = _agentSummaries.ComposeMessage("Agent changes");
            IncludeAgentSummaries.IsEnabled = _canIncludeAgentSummaries;
            IncludeAgentSummaries.IsChecked = _canIncludeAgentSummaries && _includeAgentSummaries;
            UseAgentSummaries.IsEnabled = _canIncludeAgentSummaries;
            UpdateAgentSummaryHint();
            MessagePreview.IsExpanded = _canIncludeAgentSummaries;
        }
        _ready = true;
        UpdatePreview();
        Loaded += (_, _) =>
        {
            if (_mode == GitCommitMode.PrepareSync || _freezeDestination)
            {
                Submit.Focus();
                return;
            }
            TargetBranchBox.Focus();
            TargetBranchBox.SelectAll();
        };
        Closed += (_, _) =>
        {
            if (_mode != GitCommitMode.PrepareSync && _snapshot.RepositoryRoot is { } previewRoot && _previewConnectionId is { } previewConnection)
                GitCommitPreviewState.EndReview(previewRoot, _snapshot.Branch, previewConnection);
        };
    }

    public string TargetBranch => _mode == GitCommitMode.PrepareSync ? _snapshot.Branch
        : _freezeDestination ? _fixedDestination : TargetBranchBox.Text.Trim();
    public string Message => MessageBox.Text;
    public bool MessageWasEdited => _messageEdited;
    public AgentGitSummaryBatch? ReviewedAgentSummaries => _canIncludeAgentSummaries && IncludeAgentSummaries.IsChecked == true
        && _reviewedAgentSummaries is { Entries.Count: > 0 } ? _reviewedAgentSummaries : null;

    private void ConfigureMode()
    {
        var changedFiles = _snapshot.Changes.Count;
        const string exclusions = " Ignored untracked files remain excluded. Changes inside submodules must be committed separately; this action can record only the submodule reference.";
        switch (_mode)
        {
            case GitCommitMode.PrepareSync:
                Title = "Merge locally";
                Headline.Text = "Merge into your current branch";
                Introduction.Text = $"Fetch and merge {_sourceLabel} into {_snapshot.Branch}. This action stays local and does not push.";
                Submit.Content = "_Merge locally";
                CreationHistory.Visibility = Visibility.Collapsed;
                DestinationEditor.Visibility = Visibility.Collapsed;
                BranchPreview.Visibility = Visibility.Collapsed;
                Destination.Visibility = Visibility.Collapsed;
                Scope.Text = changedFiles > 0
                    ? $"Fetches the source first. If fetching fails, your local files and commits stay unchanged. After fetching succeeds, stages all {changedFiles:N0} changed files, including additions, modifications, and deletions, and makes a protective LOCAL checkpoint on the current branch before merging. The merge commit waits for your review. Use Commit all & push separately when you want to name a destination branch and publish your work." + exclusions
                    : "Fetches the source first. If fetching fails, your local files and commits stay unchanged. After fetching succeeds, merges the source into the current branch. Your working tree is clean, so no protective checkpoint is needed. The merge commit waits for your review. Use Commit all & push separately when you want to name a destination branch and publish your work." + exclusions;
                MessageBox.IsReadOnly = true;
                MessagePreview.Header = "Review prepared checkpoint _message";
                MessageHint.Text = "The protective checkpoint uses this prepared message automatically. A clean working tree needs no checkpoint.";
                break;
            case GitCommitMode.FinishSync:
                Title = "Commit all & push";
                Headline.Text = "Commit all & push";
                Introduction.Text = "No conflicts found. Any pending merge is still local. Review the destination, then commit all and push.";
                Submit.Content = "Commit all & _push";
                Scope.Text = "Commits the reviewed merge when a merge is pending, then pushes the destination branch. Confirmed local commits are reused; the prepared message is used only if a commit is still needed. If you choose a new destination while a merge is pending, the reviewed merge is committed locally first; a new branch is then created from that result and pushed. Existing local history is preserved." + exclusions;
                MessageHint.Text = "This message is used only if a commit is still needed. Confirmed local commits keep their existing messages. Once you edit this message, changing the destination keeps your edits.";
                break;
            default:
                Title = "Commit all & push";
                Headline.Text = "Commit all & push";
                Introduction.Text = "Review the destination. Your commit message is already prepared.";
                Submit.Content = "Commit all & _push";
                Scope.Text = $"Stages all {changedFiles:N0} changed files, including additions, modifications, and deletions, then commits and pushes the destination branch. If there is nothing to commit, it pushes the existing commits." + exclusions;
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

    private void UseAgentSummaries_Click(object sender, RoutedEventArgs e)
    {
        if (!_canIncludeAgentSummaries) return;
        _reviewedAgentSummaries = _agentSummaries;
        _includeAgentSummaries = true;
        _inclusionWasChosen = true;
        IncludeAgentSummaries.IsChecked = true;
        try { SetMessage(CreateMessage()); }
        catch (InvalidOperationException exception)
        {
            _messagePreparationError = exception.Message;
            UpdatePreview();
            return;
        }
        _messagePreparationError = null;
        _messageEdited = false;
        UpdateAgentSummaryHint();
        MessagePreview.IsExpanded = true;
        UpdatePreview();
    }

    private void IncludeAgentSummaries_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _includeAgentSummaries = IncludeAgentSummaries.IsChecked == true;
        _inclusionWasChosen = true;
        _reviewedAgentSummaries = _includeAgentSummaries ? _agentSummaries : null;
        if (!_messageEdited) SetMessage(CreateMessage());
        UpdateAgentSummaryHint();
        UpdatePreview();
    }

    private void UpdateAgentSummaryHint()
    {
        if (!_canIncludeAgentSummaries)
        {
            AgentSummaryHint.Text = "No new local commit is currently needed. These summaries stay pending when pushing existing commits.";
            return;
        }
        var reviewed = ReviewedAgentSummaries?.Entries.Count ?? 0;
        var later = (_agentSummaries?.Entries.Count ?? 0) - reviewed;
        AgentSummaryHint.Text = _messageEdited && later > 0
            ? $"This edited message includes {reviewed:N0} reviewed update(s). {later:N0} other update(s) stay pending. Use the agent summary message to rebuild and review all displayed updates."
            : "Review these changes before including them. Only the reviewed summaries are cleared after a new local commit; later updates stay pending.";
    }

    private void SetMessage(string message)
    {
        _settingMessage = true;
        try { MessageBox.Text = message; }
        finally { _settingMessage = false; }
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
        UpdateAgentSummaryHint();
        UpdatePreview();
    }

    private void UseLastCreated_Click(object sender, RoutedEventArgs e)
    {
        if (_lastCreatedBranch == null) return;
        TargetBranchBox.Text = _lastCreatedBranch;
        TargetBranchBox.Focus();
        TargetBranchBox.SelectAll();
    }

    private void UpdatePreview()
    {
        var branch = TargetBranch;
        var error = ValidateDestination(branch);
        Validation.Text = error ?? "";
        Validation.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        var current = string.Equals(branch, _snapshot.Branch, StringComparison.Ordinal);
        BranchPreview.Text = error != null ? ""
            : current ? $"Keep working on the current branch: {branch}."
            : $"Create {branch} from the current branch. The new branch carries all current commits and local files; existing local history is preserved.";
        var pushUrl = string.IsNullOrWhiteSpace(_remote.PushUrl) ? _remote.FetchUrl : _remote.PushUrl;
        Destination.Text = SensitiveDataProtection.Redact($"Push destination: {_remote.Name}/{branch}\n{pushUrl}");
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
        if (!messageValid) MessagePreview.IsExpanded = true;
        Submit.IsEnabled = error == null && messageValid;
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
