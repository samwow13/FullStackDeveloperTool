using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    private enum SetupStep { Choice, Local, Connections, Provider, Destination, Authentication, Review, CloneReview, Complete }
    private enum SetupRoute { Existing, New }
    private SetupStep _setupStep = SetupStep.Choice;
    private SetupRoute _setupRoute;
    private bool _configOpen;
    private GitCloneReview? _cloneReview;
    private bool _setupOpen = true;
    private GitHostingProvider? _setupProvider;
    private string? _trustRoot;
    private string? _localSetupError;
    private string? _verifiedTarget;
    private string _authenticationMessage = "Sign in or check your existing Git connection to continue.";
    private string _completionMessage = "";

    public Visibility SetupVisibility => GitContentAvailable && _setupOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WorkspaceVisibility => GitContentAvailable && !_setupOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConfigFolderVisibility => _configOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConfigRemoteVisibility => _configOpen && Remotes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChoiceStepVisibility => StepVisibility(SetupStep.Choice);
    public Visibility CloneReviewStepVisibility => StepVisibility(SetupStep.CloneReview);
    public Visibility SetupBackVisibility => CanSetupBack || _busy && _setupStep != SetupStep.Choice ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SetupContinueVisibility => _setupStep is SetupStep.Choice or SetupStep.Provider ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UseLocalVisibility => IsRepository ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CreateOnlineVisibility => _setupRoute == SetupRoute.New ? Visibility.Visible : Visibility.Collapsed;
    private Visibility StepVisibility(SetupStep step) => _setupStep == step ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LocalStepVisibility => StepVisibility(SetupStep.Local);
    public Visibility ConnectionsStepVisibility => StepVisibility(SetupStep.Connections);
    public Visibility ProviderStepVisibility => StepVisibility(SetupStep.Provider);
    public Visibility DestinationStepVisibility => StepVisibility(SetupStep.Destination);
    public Visibility AuthenticationStepVisibility => StepVisibility(SetupStep.Authentication);
    public Visibility ReviewStepVisibility => StepVisibility(SetupStep.Review);
    public Visibility CompleteStepVisibility => StepVisibility(SetupStep.Complete);
    public bool CanSelectRemote => IsIdle && _setupOpen && _setupStep == SetupStep.Connections && !_remoteDraftDirty;
    public string ActiveRemoteLabel => SelectedRemote is { } remote ? $"Remote: {remote.Name}" : "No active remote";
    public bool CanTrustFolder => _trustRoot != null && IsIdle;
    private bool HasSetupDrafts => _remoteDraftDirty || _drafts.Any(pair => !string.Equals(pair.Key, _displayedFolder, StringComparison.OrdinalIgnoreCase) && pair.Value.RemoteDirty);
    public bool CanSetupBack => IsIdle && _setupStep is SetupStep.Local or SetupStep.Provider or SetupStep.Destination or SetupStep.Authentication or SetupStep.Review or SetupStep.CloneReview;
    public bool CanSetupNext => IsIdle && (_setupStep switch
    {
        SetupStep.Local => IsRepository || CanInitialize,
        SetupStep.Connections => IsRepository,
        SetupStep.Provider => _setupProvider != null,
        SetupStep.Destination => !string.IsNullOrWhiteSpace(RemoteUrl?.Text) && !string.IsNullOrWhiteSpace(RemoteName?.Text),
        SetupStep.Authentication or SetupStep.Review => ConnectionVerified,
        SetupStep.CloneReview => _cloneReview != null,
        SetupStep.Complete => true,
        _ => false
    });
    public string SetupProgress => _setupStep switch
    {
        SetupStep.Choice or SetupStep.Local => "GIT SETUP",
        SetupStep.Connections => "GIT CONFIG",
        SetupStep.Provider => "CHOOSE PROVIDER",
        SetupStep.Destination => "CONNECT REPOSITORY",
        SetupStep.Authentication => "SIGN IN",
        SetupStep.Review or SetupStep.CloneReview => "REVIEW",
        _ => "GIT SETUP · CONNECTED"
    };
    public string SetupTitle => _setupStep switch
    {
        SetupStep.Choice => IsRepository ? "This repository is not connected online yet." : "This folder is not configured for Git yet.",
        SetupStep.Local => "Create your local repository",
        SetupStep.Connections => "Your Git connections",
        SetupStep.Provider => "Where is your repository hosted?",
        SetupStep.Destination => _setupRoute == SetupRoute.New ? $"Create an empty repository on {ProviderLabel}" : "Paste your repository URL",
        SetupStep.Authentication => $"Connect to {ProviderLabel}",
        SetupStep.Review => "Review your connection",
        SetupStep.CloneReview => "Bring this repository into your folder",
        _ => "Your connection is ready"
    };
    public string SetupDescription => _setupStep switch
    {
        SetupStep.Choice => "Choose how to get started.",
        SetupStep.Local => "Your files stay local. Next, we'll connect an online repository.",
        SetupStep.Connections => "Choose or manage your online connection.",
        SetupStep.Provider => "Choose where your online repository lives.",
        SetupStep.Destination => _setupRoute == SetupRoute.New ? "Leave README, license and .gitignore unchecked, then copy its clone URL." : "Copy the HTTPS or SSH clone URL from your online repository.",
        SetupStep.Authentication => "Use your Git credentials for this destination. The launcher does not save your password or token.",
        SetupStep.Review => "Check the destination before saving.",
        SetupStep.CloneReview => "Clone downloads history and files into an empty folder.",
        _ => "You can now open your Git workspace."
    };
    public string LocalSetupMessage => _trustRoot != null
        ? $"{_localSetupError}\n\nRepository: {_trustRoot}\n\nOnly trust this repository if you recognize its source and trust its contents. Trusting it adds this exact path to your Windows user's global Git safe.directory list."
        : _localSetupError ?? "Choose the name of your first branch.";
    public string ProviderLabel => _setupProvider switch
    {
        GitHostingProvider.GitHub => "GitHub",
        GitHostingProvider.AzureDevOps => "Azure DevOps",
        _ => "a provider"
    };
    public string DestinationHint => _setupProvider == GitHostingProvider.AzureDevOps
        ? "Azure DevOps: Repos → Files → Clone."
        : "GitHub: open the repository → Code → copy its clone URL.";
    public string CloneReviewText => _cloneReview == null ? "" : SensitiveDataProtection.Redact($"Online repository\n{_cloneReview.CloneUrl}\n\nEmpty destination folder\n{_cloneReview.Folder}\n\nGit may ask you to sign in. No commits will be pushed. If cloning fails, partial files stay in place; inspect them before starting again.");
    public string AuthDestination => SensitiveDataProtection.Redact(RemoteUrl?.Text.Trim() ?? "");
    public string AuthenticationHint => IsSshTarget
        ? $"This {ProviderLabel} connection uses SSH. Add your public key to your provider account, load the private key into your SSH agent, and verify the server's host key with Git. Then choose Check connection. SSH checks never accept an unknown host key automatically."
        : _setupProvider == GitHostingProvider.AzureDevOps
            ? "Connect with the Microsoft account that has access to this Azure DevOps organization. Git Credential Manager reuses an existing account or opens sign-in when needed. An Azure CLI login alone does not establish Git access. Install Git for Windows with Git Credential Manager if it is missing, then retry."
            : "Sign in with the GitHub account that has access to this domain and repository. Git Credential Manager handles sign-in; your organization may also require SSO authorization. Install Git for Windows with Git Credential Manager if it is missing, then retry.";
    public string AuthenticationMessage => _authenticationMessage;
    public string SignInLabel => IsSshTarget ? "Check SSH connection"
        : _setupProvider == GitHostingProvider.AzureDevOps ? "Connect with Git…" : "Sign in to GitHub…";
    public Visibility AzureAccountHelpVisibility => _setupProvider == GitHostingProvider.AzureDevOps && !IsSshTarget ? Visibility.Visible : Visibility.Collapsed;
    public string AzureAccountHelpText
    {
        get
        {
            string organization;
            try { organization = GitConnectionService.ParseTarget(GitHostingProvider.AzureDevOps, RemoteUrl?.Text.Trim() ?? "").Organization; }
            catch { return "Enter a valid Azure DevOps clone URL first."; }
            return $"To choose another Microsoft account with Git Credential Manager's OAuth mode, run this from the local repository, replacing ACCOUNT_EMAIL with your account:\n\ngit credential-manager azure-repos bind --local {organization} ACCOUNT_EMAIL\n\nThen choose Connect with Git. This binding applies to this repository. If Git is configured to use a personal access token, account bindings do not select that token; update the matching stored credential through Windows Credential Manager. Open the account guide below for those steps.";
        }
    }
    public string CompletionMessage => _completionMessage;
    public string SetupNextLabel => _setupStep switch
    {
        SetupStep.Local when CanInitialize => "Create local repository…",
        SetupStep.Connections => "Return to workspace",
        SetupStep.Review => "Save connection",
        SetupStep.CloneReview => "Clone repository…",
        SetupStep.Complete => "Open Git workspace",
        _ => "Continue"
    };
    public string SetupReview
    {
        get
        {
            var name = RemoteName?.Text.Trim() ?? "";
            var previous = Remotes.FirstOrDefault(remote => remote.Name == name);
            var change = previous == null ? "This adds a new remote. Existing connections stay in place."
                : $"This updates remote '{name}'.\nPrevious fetch: {previous.FetchUrl}\nPrevious push: {previous.PushUrl}";
            return SensitiveDataProtection.Redact($"Local repository\n{Root}\n\nOnline repository\n{AuthDestination}\n\nConnection: {name}\n{change}\n\nRead access verified. Push permission is checked when you push. Saving does not upload or merge files.");
        }
    }
    private bool IsSshTarget
    {
        get
        {
            try { return _setupProvider is { } provider && GitConnectionService.ParseTarget(provider, RemoteUrl?.Text.Trim() ?? "").IsSsh; }
            catch { return false; }
        }
    }
    private string ConnectionKey(string url) => $"{Root}\n{_setupProvider}\n{url}";
    private bool ConnectionVerified
    {
        get
        {
            try { return _setupProvider is { } provider && _verifiedTarget == ConnectionKey(GitConnectionService.ValidateUrl(provider, RemoteUrl?.Text.Trim() ?? "")); }
            catch { return false; }
        }
    }
    private void InvalidateConnectionCheck()
    {
        _verifiedTarget = null;
        _authenticationMessage = "Sign in or check your existing Git connection to continue.";
    }
    private void OpenSetup(SetupStep step)
    {
        _setupOpen = true;
        _setupStep = step;
        Changed();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_setupOpen || _busy || !GitContentAvailable) return;
            SetupScroll.ScrollToTop();
            if (_setupStep == SetupStep.Destination) RemoteUrl.Focus();
            else if (_setupStep == SetupStep.Local && CanInitialize) InitialBranch.Focus();
            else if (_setupStep == SetupStep.Choice) ExistingRepositoryChoice.Focus();
            else if (_setupStep == SetupStep.Provider) GitHubChoice.Focus();
            else if (_setupStep == SetupStep.Connections) ConnectionPicker.Focus();
            else if (_setupStep == SetupStep.Authentication) SetupSignIn.Focus();
            else SetupContinue.Focus();
        }));
    }

    // Reuse startup verification for this exact destination, or join its silent in-flight check.
    // Refresh local status remains entirely local; explicit sign-in is a separate action.
    private async Task EnsureSetupAsync(bool verifyConnection = true)
    {
        if (_busy || _gitLoadFailed) return;
        if (!IsRepository)
        {
            OpenSetup(_remoteDraftDirty ? _setupStep : _setupStep == SetupStep.Local ? SetupStep.Local : SetupStep.Choice);
            return;
        }
        if (HasSyncRecovery) { ShowLocalWorkspace(); return; }
        if (_remoteDraftDirty) { OpenSetup(_setupStep == SetupStep.Local ? SetupStep.Provider : _setupStep); return; }
        if (SelectedRemote is not { } remote)
        {
            if (Remotes.Count > 0)
            {
                OpenSetup(SetupStep.Connections);
                ConnectionPicker.SelectedItem = null;
                SetStatus("Select the active Git connection, then choose Use selected connection.");
            }
            else OpenSetup(SetupStep.Choice);
            return;
        }
        if (!verifyConnection)
        {
            if (_configOpen) OpenSetup(SetupStep.Connections);
            else ShowLocalWorkspace();
            return;
        }
        BeginConnection(remote);
        if (_setupProvider is not { } provider || Root is not { } root) return;
        if (remote.FetchUrl == remote.PushUrl && GitConnectionWarmup.TryGetReadyForSession(root, provider, remote.FetchUrl, out _))
        {
            _authenticationMessage = "Connection verified earlier this session. Git checks access again for each network operation.";
            _verifiedTarget = ConnectionKey(GitConnectionService.ValidateUrl(provider, remote.FetchUrl));
            ShowLocalWorkspace();
            SetStatus(_authenticationMessage);
            return;
        }
        if (GitConnectionWarmup.TryGet(root, provider, remote.FetchUrl, out var cached))
        {
            _authenticationMessage = cached.Message;
            if (cached.Ready) _verifiedTarget = ConnectionKey(GitConnectionService.ValidateUrl(provider, remote.FetchUrl));
            if (cached.Ready && remote.FetchUrl == remote.PushUrl) ShowLocalWorkspace();
            else OpenSetup(SetupStep.Authentication);
            return;
        }
        OpenSetup(SetupStep.Authentication);
        if (await VerifySetupConnectionAsync(false, reuseStartupCheck: true) && remote.FetchUrl == remote.PushUrl)
        {
            _setupOpen = false;
            WorkspaceTabs.SelectedItem = WorkTab;
            Changed();
        }
    }

    private void Config_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _configOpen = true;
        OpenSetup(_remoteDraftDirty ? _setupStep : IsRepository && Remotes.Count > 0 ? SetupStep.Connections : SetupStep.Choice);
        ConnectionPicker.SelectedItem = Remotes.FirstOrDefault(remote => remote.Name == SelectedRemote?.Name) ?? Remotes.FirstOrDefault();
    }
    private void Connection_Changed(object sender, SelectionChangedEventArgs e) => Changed();
    private async void UseConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Root is not { } root || !CanLeaveConnectionDraft()) return;
        if (ConnectionPicker.SelectedItem is not GitRemoteInfo remote)
        {
            SetStatus("Select a connection first.");
            return;
        }
        if (!await SaveAgentConnectionSelectionAsync(root, remote)) return;
        _remoteDraftDirty = false;
        _preferredRemote = remote.Name;
        await LoadGitWorkspaceAsync();
    }
    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !CanLeaveConnectionDraft()) return;
        if (ConnectionPicker.SelectedItem is GitRemoteInfo remote) { _setupRoute = SetupRoute.Existing; BeginConnection(remote); }
        else SetStatus("Select a connection first, or choose Add connection.");
    }
    private void AddConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !CanLeaveConnectionDraft()) return;
        _setupRoute = SetupRoute.Existing;
        BeginConnection(null);
    }
    private bool CanLeaveConnectionDraft() => !_remoteDraftDirty || Confirm("Discard connection draft", "Discard the unsaved connection details and start another connection? Saved Git remotes will remain unchanged.");
    private void BeginConnection(GitRemoteInfo? remote)
    {
        _applyingSnapshot = true;
        try
        {
            RemoteName.Text = remote?.Name ?? NextRemoteName();
            RemoteUrl.Text = remote is { UrlCanCopy: true } ? remote.FetchUrl : "";
            _setupProvider = remote is { UrlCanCopy: true } ? GitConnectionService.InferProvider(remote.FetchUrl) ?? remote.Provider : null;
            _remoteDraftDirty = false;
            _cloneReview = null;
            InvalidateConnectionCheck();
        }
        finally { _applyingSnapshot = false; }
        OpenSetup(SetupStep.Provider);
    }
    private string NextRemoteName()
    {
        if (Remotes.All(remote => remote.Name != "origin")) return "origin";
        for (var i = 2; ; i++)
        {
            var name = $"connection-{i}";
            if (Remotes.All(remote => remote.Name != name)) return name;
        }
    }
    private void GitHubProvider_Click(object sender, RoutedEventArgs e) => ChooseProvider(GitHostingProvider.GitHub);
    private void AzureProvider_Click(object sender, RoutedEventArgs e) => ChooseProvider(GitHostingProvider.AzureDevOps);
    private void ChooseProvider(GitHostingProvider provider)
    {
        if (_busy) return;
        if (_setupProvider != provider)
        {
            _setupProvider = provider;
            InvalidateConnectionCheck();
        }
        _cloneReview = null;
        _remoteDraftDirty = true;
        OpenSetup(SetupStep.Destination);
    }
    private void ExistingRepository_Click(object sender, RoutedEventArgs e) => StartRepositorySetup(SetupRoute.Existing);
    private void NewRepository_Click(object sender, RoutedEventArgs e) => StartRepositorySetup(SetupRoute.New);
    private void StartRepositorySetup(SetupRoute route)
    {
        if (_busy || !CanLeaveConnectionDraft()) return;
        _setupRoute = route;
        BeginConnection(null);
        if (route == SetupRoute.New && CanInitialize) OpenSetup(SetupStep.Local);
    }
    private void CreateOnlineRepository_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var url = _setupProvider == GitHostingProvider.AzureDevOps ? "https://dev.azure.com/" : "https://github.com/new";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { SetStatus("Your browser could not open. Open your provider and create an empty repository, then return with its clone URL."); }
    }
    private async void TrustFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _trustRoot is not { } root) return;
        if (!Confirm("Trust this Git repository", $"Git reports a different owner for:\n{root}\n\nOnly continue if you trust this repository and its contents. Add this exact path to your Windows user's global Git safe.directory list? No wildcard or other folder will be trusted.")) return;
        await ExecuteAsync("Trust selected repository", token => GitRepositoryService.TrustRepositoryAsync(root, token));
        if (!_gitLoadFailed && IsRepository) await LoadGitWorkspaceAsync();
    }
    private async void SignIn_Click(object sender, RoutedEventArgs e) => await VerifySetupConnectionAsync(!IsSshTarget);
    private async void VerifyConnection_Click(object sender, RoutedEventArgs e) => await VerifySetupConnectionAsync(false);
    private void AccountHelp_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        const string url = "https://github.com/git-ecosystem/git-credential-manager/blob/main/docs/azrepos-users-and-tokens.md";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { SetStatus("The account guide could not open in your browser. Check your default browser settings and retry."); }
    }
    private string ValidateSetupDestination()
    {
        if (_setupProvider is not { } provider) throw new InvalidOperationException("Choose GitHub or Azure DevOps first.");
        var name = RemoteName.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]{0,100}$") || name.Contains("..", StringComparison.Ordinal) || name.EndsWith('.') || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use a connection name such as origin or work-github, with letters, numbers, dots, hyphens or underscores.");
        if (Remotes.FirstOrDefault(remote => remote.Name == name) is { } existing && existing.FetchUrl != existing.PushUrl)
            throw new InvalidOperationException("This remote has separate fetch and push destinations. Use a new connection name, or align those destinations with Git before reconnecting it here.");
        return GitConnectionService.ValidateUrl(provider, RemoteUrl.Text.Trim());
    }
    private async Task<bool> VerifySetupConnectionAsync(bool signIn, bool reuseStartupCheck = false)
    {
        if (_busy || Root is not { } root) return false;
        string url;
        try { url = ValidateSetupDestination(); }
        catch (Exception ex) { SetStatus(SafeError(ex)); return false; }
        var provider = _setupProvider!.Value;
        var key = ConnectionKey(url);
        InvalidateConnectionCheck();
        GitConnectionCheckResult? check = null;
        var success = await ExecuteAsync(signIn ? $"Sign in to {ProviderLabel}" : "Check Git connection", async token =>
        {
            check = signIn ? await GitConnectionService.SignInAsync(root, provider, url, token)
                : reuseStartupCheck ? await GitConnectionWarmup.GetOrCheckAsync(root, provider, url, token)
                : await GitConnectionService.CheckAsync(root, provider, url, token);
            return check.Message;
        }, refresh: false, useResultAsStatus: true);
        if (success && check != null)
        {
            _authenticationMessage = check.Message;
            if (check.Authenticated && check.RepositoryAccessible) _verifiedTarget = key;
        }
        else _authenticationMessage = "Connection could not be verified. Review the setup details below, then retry or go Back to correct the URL.";
        Changed();
        return ConnectionVerified;
    }
    private void SetupBack_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSetupBack) return;
        OpenSetup(_setupStep switch
        {
            SetupStep.Review => SetupStep.Authentication,
            SetupStep.Authentication => SetupStep.Destination,
            SetupStep.Destination => SetupStep.Provider,
            SetupStep.CloneReview => SetupStep.Destination,
            _ => _configOpen && Remotes.Count > 0 ? SetupStep.Connections : SetupStep.Choice
        });
    }
    private async void SetupNext_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSetupNext) return;
        switch (_setupStep)
        {
            case SetupStep.Local:
                if (IsRepository)
                {
                    _setupRoute = SetupRoute.New;
                    BeginConnection(null);
                    OpenSetup(SetupStep.Provider);
                    break;
                }
                if (CanInitialize && SelectedFolder is { } folder)
                {
                    var branch = InitialBranch.Text.Trim();
                    if (!Confirm("Initialize Git", $"Create a new local Git repository in:\n{folder.Directory}\n\nInitial branch: {branch}\n\nExisting files will not be committed or uploaded.")) return;
                    var created = await ExecuteAsync("Initialize repository", token => GitRepositoryService.InitializeAsync(folder.Directory, branch, token),
                        completed: _ => { _setupRoute = SetupRoute.New; _remoteDraftDirty = true; });
                    if (created && !_gitLoadFailed && IsRepository) OpenSetup(SetupStep.Provider);
                }
                break;
            case SetupStep.Provider: OpenSetup(SetupStep.Destination); break;
            case SetupStep.Destination:
                try
                {
                    var url = ValidateSetupDestination();
                    if (!IsRepository && _setupRoute == SetupRoute.Existing && SelectedFolder is { } cloneFolder)
                    {
                        GitCloneReview? review = null;
                        if (await ExecuteAsync("Check clone folder", async token =>
                        {
                            review = await GitRepositoryService.PrepareCloneAsync(cloneFolder.Directory, _setupProvider!.Value, url, token);
                            return "Empty folder ready for cloning.";
                        }, refresh: false))
                        {
                            _cloneReview = review;
                            OpenSetup(SetupStep.CloneReview);
                        }
                    }
                    else OpenSetup(SetupStep.Authentication);
                }
                catch (Exception ex) { SetStatus(SafeError(ex)); }
                break;
            case SetupStep.CloneReview:
                if (_cloneReview is not { } clone || !Confirm("Clone repository", CloneReviewText)) return;
                var cloned = await ExecuteAsync("Clone repository", token => GitRepositoryService.CloneAsync(clone, token),
                    completed: _ => { _remoteDraftDirty = false; _preferredRemote = "origin"; _cloneReview = null; }, useResultAsStatus: true);
                _cloneReview = null;
                if (!cloned)
                {
                    if (IsRepository) { _remoteDraftDirty = false; OpenSetup(SetupStep.Authentication); }
                    else OpenSetup(SetupStep.Destination);
                }
                if (cloned && !_gitLoadFailed && IsRepository) await LoadGitWorkspaceAsync();
                break;
            case SetupStep.Authentication: OpenSetup(SetupStep.Review); break;
            case SetupStep.Review: await SaveSetupConnectionAsync(); break;
            case SetupStep.Connections:
            case SetupStep.Complete: ShowLocalWorkspace(); break;
        }
    }
    private async Task SaveSetupConnectionAsync()
    {
        if (!ConnectionVerified || Root is not { } root) return;
        string url;
        try { url = ValidateSetupDestination(); }
        catch (Exception ex) { SetStatus(SafeError(ex)); return; }
        var name = RemoteName.Text.Trim();
        var previous = Remotes.FirstOrDefault(remote => remote.Name == name);
        if (previous != null && (previous.FetchUrl != url || previous.PushUrl != url) &&
            !Confirm("Replace connection destination", $"Repository: {root}\nConnection: {name}\nCurrent fetch: {previous.FetchUrl}\nCurrent push: {previous.PushUrl}\n\nNew fetch and push: {url}\n\nReplace this connection's destination?")) return;
        var provider = _setupProvider;
        var saveDetail = "";
        var saved = await ExecuteAsync("Save Git connection", token => GitRepositoryService.SaveRemoteAsync(root, name, url, previous != null, token, provider: provider),
            completed: result => { _remoteDraftDirty = false; _preferredRemote = name; saveDetail = result; }, useResultAsStatus: true);
        if (!saved || _gitLoadFailed || !IsRepository) return;
        if (SelectedRemote is { } selected && !await SaveAgentConnectionSelectionAsync(root, selected))
            saveDetail += "\nThe Git connection was saved, but its active selection for agents could not be saved. Review error details below and select this connection again before reporting changes.";
        _completionMessage = "Online connection saved. Your files stay local until you commit and push.";
        SetStatus(saveDetail);
        OpenSetup(SetupStep.Complete);
    }
    private void LocalWorkspace_Click(object sender, RoutedEventArgs e) => ShowLocalWorkspace();
    private void ShowLocalWorkspace()
    {
        if (_busy || _gitLoadFailed || !IsRepository) return;
        _setupOpen = false;
        _configOpen = false;
        WorkspaceTabs.SelectedItem = WorkTab;
        SetStatus(_remoteDraftDirty ? "Connection draft kept for this session. Choose Config to continue setup." : "Git workspace ready. Choose Config to manage connections.");
        Changed();
        ScheduleMissingReleaseBranchPrompt();
    }
}
