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
    public bool CanRetryGit => !_busy && _gitLoadDepth == 0;
    private string _gitLoadFailure = "Git could not load. Try again.";
    private bool IsGitLoading => _initialGitLoad || _gitLoadDepth > 0;
    private bool GitContentAvailable => !IsGitLoading && !_gitLoadFailed && _repositoryDiscoveryComplete && !_selectingRepository;
    public Visibility GitLoadingVisibility => IsGitLoading ? Visibility.Visible : Visibility.Collapsed;
    public bool AnimateGitLoading => IsGitLoading && SystemParameters.ClientAreaAnimation;
    public Visibility GitFailureVisibility => !IsGitLoading && _gitLoadFailed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GitHeaderActionsVisibility => GitContentAvailable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BranchHeaderVisibility => GitContentAvailable && (!_setupOpen || _setupWindow != null && IsVisible) && IsRepository
        ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GitStatusVisibility => GitContentAvailable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GitErrorVisibility => !IsGitLoading && !string.IsNullOrEmpty(_latestErrorDetails) ? Visibility.Visible : Visibility.Collapsed;
    public string GitLoadFailure => _gitLoadFailure;
    public Visibility TrustFailureVisibility => _trustRoot != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CachedTrustFailureVisibility => _snapshot != null && _trustRoot != null && !_gitLoadFailed
        ? Visibility.Visible : Visibility.Collapsed;
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
        if (_gitLoadDepth == 0) ScheduleMissingReleaseBranchPrompt();
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
        {
            _cacheRefreshFailed = true;
            if (_snapshot == null) FailGitLoad("Git status could not finish loading. Try again.");
        }
    }

    private async Task LoadGitWorkspaceAsync(bool verifyConnection = true, bool forceRefresh = false)
    {
        if (_busy || _gitLoadDepth > 0) return;
        if (!await EnsureRepositorySelectedAsync(forceRefresh)) return;
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
        var loading = _snapshot == null;
        if (loading) BeginGitLoad();
        try
        {
            await RefreshAsync(showLoading: false, forceRefresh: forceRefresh);
            if (!_gitLoadFailed) await EnsureSetupAsync(verifyConnection);
            if (!_gitLoadFailed && _setupOpen && string.IsNullOrEmpty(_latestErrorDetails)) SetStatus("");
        }
        catch (Exception ex)
        {
            if (ex is GitCommandExitUnconfirmedException unconfirmed) _unconfirmedGitCommand = unconfirmed;
            ReportError(SafeError(ex));
            _cacheRefreshFailed = true;
            if (_snapshot == null || _unconfirmedGitCommand != null)
                FailGitLoad(_unconfirmedGitCommand != null ? "Git may still be running. Wait, then Retry to check again." : "Git could not finish loading. Try again.");
        }
        finally
        {
            if (loading) EndGitLoad();
            await ConsumeSharedUpdateAsync();
        }
    }

    private async void RetryGit_Click(object sender, RoutedEventArgs e) => await LoadGitWorkspaceAsync(verifyConnection: false, forceRefresh: true);
}
