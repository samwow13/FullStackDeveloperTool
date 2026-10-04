using System.Windows;
using System.Windows.Input;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    private string _publishBranchDraft = "";
    private PublishBranchScope? _publishBranchDraftScope;
    private readonly Dictionary<PublishBranchScope, string> _publishBranchDrafts = new();
    private bool _isEditingPublishBranch;
    private string _publishBranchBeforeEdit = "";
    private PublishBranchScope? _publishBranchEditingScope;

    public bool CanEditPublishBranch => IsIdle && _snapshot is { IsRepository: true };
    public bool IsEditingPublishBranch => _isEditingPublishBranch;
    private bool HasNewPublishDestination => _snapshot is { IsRepository: true, IsDetached: false } snapshot
        && !string.IsNullOrWhiteSpace(PublishBranchDraft) && PublishBranchDraft.Trim() != snapshot.Branch;
    public string PublishBranchCaption => IsEditingPublishBranch ? "Branch" : HasNewPublishDestination ? "Publish branch" : "Current branch";
    public string PublishBranchLabel => HasNewPublishDestination ? PublishBranchDraft.Trim() : BranchLabel;
    public string PublishBranchTooltip => $"Current local branch: {BranchLabel}\n{PublishBranchHint}";
    public string PublishBranchEditLabel => IsEditingPublishBranch ? "Keep branch draft (Enter); cancel with Escape" : "Edit or switch branch";
    public Visibility PublishBranchReadoutVisibility => IsEditingPublishBranch ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PublishBranchEditorVisibility => IsEditingPublishBranch ? Visibility.Visible : Visibility.Collapsed;
    public string MergeTargetLabel => $"Merge into current local branch: {BranchLabel}";

    public string PublishBranchDraft
    {
        get => _publishBranchDraft;
        set
        {
            value ??= "";
            if (_publishBranchDraft == value) return;
            _publishBranchDraft = value;
            if (_publishBranchDraftScope is { } scope) _publishBranchDrafts[scope] = value;
            Changed(nameof(PublishBranchDraft));
            Changed(nameof(PublishBranchHint));
            Changed(nameof(PublishBranchLabel));
            Changed(nameof(PublishBranchCaption));
            Changed(nameof(PublishBranchTooltip));
            Changed(nameof(BranchActionText));
            Changed(nameof(CanCreateNamedBranch));
            Changed(nameof(CanSwitchNamedBranch));
        }
    }

    public string PublishBranchHint => !IsRepository ? "Select a repository in Config first."
        : _snapshot!.IsDetached ? "Enter a local branch name and choose Switch branch, or create a new branch from this commit."
        : string.IsNullOrWhiteSpace(PublishBranchDraft) ? "Enter the current branch or a new branch for Commit all & push."
        : PublishBranchDraft.Trim() == _snapshot.Branch ? "Commit all & push uses this branch."
        : Branches.Any(branch => !branch.IsRemote && branch.Name.Equals(PublishBranchDraft.Trim(), StringComparison.OrdinalIgnoreCase))
            ? "Choose Switch branch to use this existing local branch."
        : SelectedRemote is { } remote && Branches.Any(branch => branch.IsRemote
            && branch.Name.Equals($"{remote.Name}/{PublishBranchDraft.Trim()}", StringComparison.Ordinal))
            ? "Choose Switch branch to create a local tracking branch from the last fetched remote history."
            : "Commit all & push creates this new branch from your current history. Editing this field does not rename or switch branches.";

    private void SynchronizePublishBranchDraft()
    {
        var nextScope = CurrentPublishBranchScope();
        if (nextScope != _publishBranchDraftScope) EndPublishBranchEdit();
        if (nextScope == null || nextScope == _publishBranchDraftScope) return;
        if (_publishBranchDraftScope is { } previous) _publishBranchDrafts[previous] = _publishBranchDraft;
        _publishBranchDraftScope = nextScope;
        _publishBranchDraft = _publishBranchDrafts.GetValueOrDefault(nextScope, nextScope.IsDetached ? "" : _snapshot!.Branch);
        Changed(nameof(PublishBranchDraft));
        Changed(nameof(PublishBranchHint));
        Changed(nameof(PublishBranchLabel));
        Changed(nameof(PublishBranchCaption));
        Changed(nameof(PublishBranchTooltip));
    }

    private void PublishBranchEdit_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditPublishBranch) return;
        if (IsEditingPublishBranch)
        {
            EndPublishBranchEdit(returnFocus: true);
            return;
        }
        _publishBranchBeforeEdit = PublishBranchDraft;
        _publishBranchEditingScope = _publishBranchDraftScope;
        _isEditingPublishBranch = true;
        Changed();
        PublishBranchName.Focus();
        PublishBranchName.SelectAll();
    }

    private void PublishBranchName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        EndPublishBranchEdit(cancel: e.Key == Key.Escape, returnFocus: true);
        e.Handled = true;
    }

    private void EndPublishBranchEdit(bool cancel = false, bool returnFocus = false)
    {
        if (!IsEditingPublishBranch) return;
        if (cancel && _publishBranchEditingScope == _publishBranchDraftScope)
            PublishBranchDraft = _publishBranchBeforeEdit;
        else if (!cancel && _publishBranchEditingScope != null
            && _publishBranchEditingScope == _publishBranchDraftScope)
            PublishBranchDraft = string.IsNullOrWhiteSpace(PublishBranchDraft)
                ? _publishBranchEditingScope.IsDetached ? "" : _publishBranchEditingScope.Branch : PublishBranchDraft.Trim();
        _isEditingPublishBranch = false;
        _publishBranchEditingScope = null;
        _publishBranchBeforeEdit = "";
        Changed();
        if (returnFocus) PublishBranchEdit.Focus();
    }

    private PublishBranchScope? CurrentPublishBranchScope()
    {
        if (_snapshot is not { IsRepository: true, RepositoryRoot: { } root } snapshot) return null;
        var connection = "";
        if (SelectedRemote is { } remote)
        {
            try { connection = AgentGitChangeStore.ConnectionId(remote); }
            catch (InvalidOperationException) { connection = "unsupported:" + remote.Name; }
        }
        return new(root.ToUpperInvariant(), snapshot.Branch, connection, snapshot.IsDetached);
    }

    private void RestorePublishBranchDraft(string draft, PublishBranchScope? scope)
    {
        EndPublishBranchEdit();
        _publishBranchDraftScope = scope;
        _publishBranchDraft = scope != null && _publishBranchDrafts.TryGetValue(scope, out var retained) ? retained : draft;
        if (scope != null) _publishBranchDrafts[scope] = _publishBranchDraft;
    }

    private void ResetPublishBranchDraftAfterSuccess()
    {
        EndPublishBranchEdit();
        if (_publishBranchDraftScope is { } scope) _publishBranchDrafts.Remove(scope);
        _publishBranchDraftScope = null;
        _publishBranchDraft = "";
        Changed(nameof(PublishBranchDraft));
        Changed(nameof(PublishBranchHint));
    }

    private sealed record PublishBranchScope(string Root, string Branch, string ConnectionId, bool IsDetached);
}
