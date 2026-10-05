using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    private readonly HashSet<ReleasePromptScope> _releasePromptedScopes = [];
    private bool _releasePromptScheduled;
    private bool _releasePromptOpen;

    private sealed record ReleasePromptScope(string Root, string ConnectionId);

    private void ScheduleMissingReleaseBranchPrompt()
    {
        if (_releasePromptScheduled || _releasePromptOpen) return;
        _releasePromptScheduled = true;
        // Let loading/setup finish and paint before opening the source chooser.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(async () =>
        {
            _releasePromptScheduled = false;
            try
            {
                if (!IsLoaded || !IsVisible || !IsEnabled || !IsActive || !GitContentAvailable
                    || _setupOpen || _configOpen || _remoteDraftDirty || !CanChangeReleaseBranch
                    || _powerDetails is not { ReleaseBranch: null, SuggestedReleaseBranch: null }
                    || Root is not { } root || SelectedRemote is not { } remote) return;
                var scope = new ReleasePromptScope(root.ToUpperInvariant(), AgentGitChangeStore.ConnectionId(remote));
                if (!_releasePromptedScopes.Add(scope)) return;
                // Claim this scope before ShowDialog enters a nested dispatcher loop.
                // Canceling leaves Git available without reopening on every refresh.
                _releasePromptOpen = true;
                var chosen = await ChooseReleaseBranchAsync(GitReleaseBranchMode.ChooseMissing);
                // Canceling the picker's local refresh can invalidate its owner's
                // snapshot. Restore local status without retrying remote work.
                if (!chosen && _gitLoadFailed && IsIdle && _unconfirmedGitCommand is null)
                    await RefreshAsync(fetchRemote: false);
            }
            catch (Exception exception)
            {
                if (exception is GitCommandExitUnconfirmedException unconfirmed)
                    HoldUnconfirmedBranchCommand(unconfirmed);
                ReportError(SafeError(exception));
                SetStatus("Release branch selection could not finish. Choose the source in Config when ready.");
            }
            finally { _releasePromptOpen = false; }
        }));
    }
}
