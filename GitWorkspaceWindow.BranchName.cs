using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    private string _publishBranchDraft = "";
    private PublishBranchScope? _publishBranchDraftScope;
    private readonly Dictionary<PublishBranchScope, string> _publishBranchDrafts = new();

    public bool CanEditPublishBranch => IsIdle && _snapshot is { IsRepository: true, IsDetached: false };

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
        }
    }

    public string PublishBranchHint => !IsRepository ? "Select a repository in Config first."
        : _snapshot!.IsDetached ? "Switch to a local branch in Branches before choosing a destination."
        : string.IsNullOrWhiteSpace(PublishBranchDraft) ? "Enter the current branch or a new branch for Commit all & push."
        : PublishBranchDraft.Trim() == _snapshot.Branch ? "Commit all & push uses this branch."
        : Branches.Any(branch => !branch.IsRemote && branch.Name.Equals(PublishBranchDraft.Trim(), StringComparison.OrdinalIgnoreCase))
            ? "That local branch already exists. Choose the current branch or a new name, or switch in Branches."
        : SelectedRemote is { } remote && Branches.Any(branch => branch.IsRemote
            && branch.Name.Equals($"{remote.Name}/{PublishBranchDraft.Trim()}", StringComparison.Ordinal))
            ? "Last fetched remote history contains this branch. Choose the current branch or a new name, or switch in Branches."
            : "Commit all & push creates this new branch from your current history. Editing this field does not rename or switch branches.";

    private void SynchronizePublishBranchDraft()
    {
        var nextScope = CurrentPublishBranchScope();
        if (nextScope == null || nextScope == _publishBranchDraftScope) return;
        if (_publishBranchDraftScope is { } previous) _publishBranchDrafts[previous] = _publishBranchDraft;
        _publishBranchDraftScope = nextScope;
        _publishBranchDraft = _publishBranchDrafts.GetValueOrDefault(nextScope, _snapshot!.Branch);
        Changed(nameof(PublishBranchDraft));
        Changed(nameof(PublishBranchHint));
    }

    private PublishBranchScope? CurrentPublishBranchScope()
    {
        if (_snapshot is not { IsRepository: true, IsDetached: false, RepositoryRoot: { } root } snapshot) return null;
        var connection = "";
        if (SelectedRemote is { } remote)
        {
            try { connection = AgentGitChangeStore.ConnectionId(remote); }
            catch (InvalidOperationException) { connection = "unsupported:" + remote.Name; }
        }
        return new(root.ToUpperInvariant(), snapshot.Branch, connection);
    }

    private void RestorePublishBranchDraft(string draft, PublishBranchScope? scope)
    {
        _publishBranchDraftScope = scope;
        _publishBranchDraft = scope != null && _publishBranchDrafts.TryGetValue(scope, out var retained) ? retained : draft;
        if (scope != null) _publishBranchDrafts[scope] = _publishBranchDraft;
    }

    private void ResetPublishBranchDraftAfterSuccess()
    {
        if (_publishBranchDraftScope is { } scope) _publishBranchDrafts.Remove(scope);
        _publishBranchDraftScope = null;
        _publishBranchDraft = "";
        Changed(nameof(PublishBranchDraft));
        Changed(nameof(PublishBranchHint));
    }

    private sealed record PublishBranchScope(string Root, string Branch, string ConnectionId);
}
