using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    private bool _initialGitLoad = true;
    private int _gitLoadDepth;
    private bool _gitLoadFailed;
    private GitCommandExitUnconfirmedException? _unconfirmedGitCommand;
    private string? _automaticFetchOutcome;
    private string? _automaticFetchError;
    public bool CanRetryGit => !_busy && _gitLoadDepth == 0;
    private string _gitLoadFailure = "Git could not load. Try again.";
    private bool IsGitLoading => _initialGitLoad || _gitLoadDepth > 0;
    private bool GitContentAvailable => !IsGitLoading && !_gitLoadFailed && _repositoryDiscoveryComplete && !_selectingRepository;
    public Visibility GitLoadingVisibility => IsGitLoading ? Visibility.Visible : Visibility.Collapsed;
    public bool AnimateGitLoading => IsGitLoading && SystemParameters.ClientAreaAnimation;
    public Visibility GitFailureVisibility => !IsGitLoading && _gitLoadFailed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GitHeaderActionsVisibility => GitContentAvailable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BranchHeaderVisibility => GitContentAvailable && !_setupOpen && IsRepository ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GitStatusVisibility => GitContentAvailable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GitErrorVisibility => !IsGitLoading && !string.IsNullOrEmpty(_latestErrorDetails) ? Visibility.Visible : Visibility.Collapsed;
    public string GitLoadFailure => _gitLoadFailure;
    public Visibility TrustFailureVisibility => _trustRoot != null ? Visibility.Visible : Visibility.Collapsed;
    public string TrustFailureMessage => $"Git reports a different owner for:\n{_trustRoot}\n\nTrust only if you recognize this repository and its contents.";

    private void BeginGitLoad()
    {
        _gitLoadDepth++;
        _initialGitLoad = false;
        _gitLoadFailed = false;
        Changed();
    }

    private void EndGitLoad()
    {
        _gitLoadDepth = Math.Max(0, _gitLoadDepth - 1);
        Changed();
        if (_gitLoadDepth == 0 && GitContentAvailable && _setupOpen && !_busy) OpenSetup(_setupStep);
    }

    private void FailGitLoad(string message)
    {
        _gitLoadFailed = true;
        _gitLoadFailure = message;
        Changed();
    }

    private void CheckGitReadFailures()
    {
        if (_comparisonError != null || _powerReadError != null)
            FailGitLoad("Git status could not finish loading. Try again.");
    }

    private async Task LoadGitWorkspaceAsync(bool verifyConnection = true)
    {
        if (_busy || _gitLoadDepth > 0) return;
        if (!await EnsureRepositorySelectedAsync()) return;
        if (_unconfirmedGitCommand is { } command)
        {
            _busy = true;
            Changed();
            var stopped = await Task.Run(command.IsExitConfirmed);
            _busy = false;
            if (!stopped)
            {
                FailGitLoad("Git may still be running. Wait, then Retry to check again.");
                return;
            }
            _unconfirmedGitCommand = null;
        }
        BeginGitLoad();
        _automaticFetchOutcome = null;
        _automaticFetchError = null;
        try
        {
            await RefreshAsync(fetchRemote: verifyConnection);
            if (!_gitLoadFailed) await EnsureSetupAsync(verifyConnection);
            // Connection verification can replace the latest-operation details. Preserve
            // the fetch failure so a successful access check cannot hide stale remote refs.
            if (_automaticFetchError != null && !_latestErrorDetails.Contains(_automaticFetchError, StringComparison.Ordinal))
                ReportError(_automaticFetchError);
            if (!_gitLoadFailed && _automaticFetchOutcome != null && (_automaticFetchError != null || (!_setupOpen && string.IsNullOrEmpty(_latestErrorDetails))))
                SetStatus(_automaticFetchOutcome);
            else if (!_gitLoadFailed && _setupOpen && string.IsNullOrEmpty(_latestErrorDetails)) SetStatus("");
        }
        catch (Exception ex)
        {
            if (ex is GitCommandExitUnconfirmedException unconfirmed) _unconfirmedGitCommand = unconfirmed;
            ReportError(SafeError(ex));
            FailGitLoad(_unconfirmedGitCommand != null ? "Git may still be running. Wait, then Retry to check again." : "Git could not finish loading. Try again.");
        }
        finally { EndGitLoad(); }
    }

    private async Task FetchForGitLoadAsync(GitRepositorySnapshot snapshot, GitRemoteInfo remote, CancellationToken token)
    {
        SetStatus($"Fetching {remote.Name} and loading Git status…");
        try
        {
            await GitRepositoryService.RunWithSnapshotAsync(snapshot,
                cancellation => GitRepositoryService.FetchAsync(snapshot.RepositoryRoot!, remote.Name, cancellation, strictSsh: true), token);
            _automaticFetchOutcome = $"Fetch complete. Branches for {remote.Name} refreshed.";
        }
        catch (GitCommandExitUnconfirmedException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _automaticFetchOutcome = $"Automatic fetch for {remote.Name} failed. Remote history uses cached refs; use Fetch to retry.";
            _automaticFetchError = _automaticFetchOutcome + "\n" + SafeError(ex);
            ReportError(_automaticFetchError);
        }
    }

    private async void RetryGit_Click(object sender, RoutedEventArgs e) => await LoadGitWorkspaceAsync();
}
