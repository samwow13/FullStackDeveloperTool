using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitWorkspaceWindow
{
    private enum SetupStep { Local, Connections, Provider, Destination, Authentication, Review, Complete }
    private SetupStep _setupStep = SetupStep.Local;
    private bool _setupOpen = true;
    private GitHostingProvider? _setupProvider;
    private string? _trustRoot;
    private string? _localSetupError;
    private string? _verifiedTarget;
    private string _authenticationMessage = "Sign in or check your existing Git connection to continue.";
    private string _completionMessage = "";

    public Visibility SetupVisibility => _setupOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WorkspaceVisibility => _setupOpen ? Visibility.Collapsed : Visibility.Visible;
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
    public bool CanSetupBack => IsIdle && _setupStep is SetupStep.Provider or SetupStep.Destination or SetupStep.Authentication or SetupStep.Review;
    public bool CanSetupNext => IsIdle && (_setupStep switch
    {
        SetupStep.Local => IsRepository || CanInitialize,
        SetupStep.Connections => IsRepository,
        SetupStep.Provider => _setupProvider != null,
        SetupStep.Destination => !string.IsNullOrWhiteSpace(RemoteUrl?.Text) && !string.IsNullOrWhiteSpace(RemoteName?.Text),
        SetupStep.Authentication or SetupStep.Review => ConnectionVerified,
        SetupStep.Complete => true,
        _ => false
    });
    public string SetupProgress => _setupStep switch
    {
        SetupStep.Local => "GIT SETUP · LOCAL FOLDER",
        SetupStep.Connections => "GIT CONFIG",
        SetupStep.Provider => "STEP 1 OF 4 · PROVIDER",
        SetupStep.Destination => "STEP 2 OF 4 · REPOSITORY",
        SetupStep.Authentication => "STEP 3 OF 4 · SIGN IN",
        SetupStep.Review => "STEP 4 OF 4 · REVIEW",
        _ => "GIT SETUP · CONNECTED"
    };
    public string SetupTitle => _setupStep switch
    {
        SetupStep.Local => "Let's get this folder ready",
        SetupStep.Connections => "Your Git connections",
        SetupStep.Provider => "Where is your repository hosted?",
        SetupStep.Destination => $"Choose your {ProviderLabel} repository",
        SetupStep.Authentication => $"Connect to {ProviderLabel}",
        SetupStep.Review => "Review your connection",
        _ => "Your connection is ready"
    };
    public string SetupDescription => _setupStep switch
    {
        SetupStep.Local => "We'll check the local folder first, then connect your account and repository one step at a time.",
        SetupStep.Connections => "Reconnect an existing destination or add another domain or organization. Each connection is a named Git remote for this repository.",
        SetupStep.Provider => "Choose one provider. The next steps will show only its setup instructions.",
        SetupStep.Destination => "Copy the clone URL of the repository that belongs with this local folder.",
        SetupStep.Authentication => "Use your Git credentials for this destination. The launcher does not save your password or token.",
        SetupStep.Review => "Check the local folder and online destination before saving. You can go Back to change either connection detail.",
        _ => "Return to your workspace, or add another connection. Config is always available at the top."
    };
    public string LocalSetupMessage => _trustRoot != null
        ? $"{_localSetupError}\n\nRepository: {_trustRoot}\n\nOnly trust this repository if you recognize its source and trust its contents. Trusting it adds this exact path to your Windows user's global Git safe.directory list."
        : _localSetupError ?? (IsRepository ? $"Repository found: {Root}"
            : CanInitialize ? $"No repository exists in this folder or its parents:\n{SelectedFolder?.Directory}\n\nIf these are new local files, choose an initial branch and initialize Git. If the project already has history elsewhere, clone it with Git first and select that project folder in the launcher."
            : SelectedFolder == null ? "No project folder is configured. Close this window and configure the project's folder first."
            : "Checking the selected folder…");
    public string ProviderLabel => _setupProvider switch
    {
        GitHostingProvider.GitHub => "GitHub",
        GitHostingProvider.AzureDevOps => "Azure DevOps",
        _ => "a provider"
    };
    public string DestinationHint => _setupProvider == GitHostingProvider.AzureDevOps
        ? "In Azure DevOps, open your organization and project, then Repos > Files > Clone. Copy the HTTPS or SSH clone URL. Example: https://dev.azure.com/organization/project/_git/repository. For a new repository, create it there first and return with its clone URL."
        : "In GitHub or GitHub Enterprise, open your repository, choose Code, and copy its HTTPS or SSH clone URL. Example: https://github.com/owner/repository.git. For a new repository, create it there first and return with its clone URL.";
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
        SetupStep.Local when CanInitialize => "Initialize this folder…",
        SetupStep.Connections => "Return to workspace",
        SetupStep.Review => "Save connection",
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
            return SensitiveDataProtection.Redact($"Local repository\n{Root}\n\nProvider\n{ProviderLabel}\n\nConnection name\n{name}\n\nFetch and push destination\n{AuthDestination}\n\n{change}\n\nAuthentication and repository read access checked. Push permission is checked when you push. Saving does not fetch, merge, commit, or push files.");
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
            if (!_setupOpen || _busy) return;
            SetupScroll.ScrollToTop();
            if (_setupStep == SetupStep.Destination) RemoteUrl.Focus();
            else if (_setupStep == SetupStep.Local && CanInitialize) InitialBranch.Focus();
            else if (_setupStep == SetupStep.Local) SetupRetry.Focus();
            else if (_setupStep == SetupStep.Provider) GitHubChoice.Focus();
            else if (_setupStep == SetupStep.Connections) ConnectionPicker.Focus();
            else if (_setupStep == SetupStep.Authentication) SetupSignIn.Focus();
            else SetupContinue.Focus();
        }));
    }

    // Reuse startup verification for this exact destination, or join its silent in-flight check.
    // Refresh local status remains entirely local; explicit sign-in is a separate action.
    private async Task EnsureSetupAsync()
    {
        if (_busy) return;
        if (!IsRepository) { OpenSetup(SetupStep.Local); return; }
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
            else BeginConnection(null);
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
        OpenSetup(!IsRepository ? SetupStep.Local : _remoteDraftDirty ? _setupStep : SetupStep.Connections);
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
        await RefreshAsync();
        await EnsureSetupAsync();
    }
    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !CanLeaveConnectionDraft()) return;
        if (ConnectionPicker.SelectedItem is GitRemoteInfo remote) BeginConnection(remote);
        else SetStatus("Select a connection first, or choose Add connection.");
    }
    private void AddConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !CanLeaveConnectionDraft()) return;
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
        _remoteDraftDirty = true;
        OpenSetup(SetupStep.Destination);
    }
    private async void LocalRetry_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        if (IsRepository) await EnsureSetupAsync();
    }
    private async void TrustFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _trustRoot is not { } root) return;
        if (!Confirm("Trust this Git repository", $"Git reports a different owner for:\n{root}\n\nOnly continue if you trust this repository and its contents. Add this exact path to your Windows user's global Git safe.directory list? No wildcard or other folder will be trusted.")) return;
        await ExecuteAsync("Trust selected repository", token => GitRepositoryService.TrustRepositoryAsync(root, token));
        if (IsRepository) await EnsureSetupAsync();
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
            _ => SetupStep.Connections
        });
    }
    private async void SetupNext_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSetupNext) return;
        switch (_setupStep)
        {
            case SetupStep.Local:
                if (CanInitialize && SelectedFolder is { } folder)
                {
                    var branch = InitialBranch.Text.Trim();
                    if (!Confirm("Initialize Git", $"Create a new local Git repository in:\n{folder.Directory}\n\nInitial branch: {branch}\n\nExisting files will not be committed or uploaded.")) return;
                    await ExecuteAsync("Initialize repository", token => GitRepositoryService.InitializeAsync(folder.Directory, branch, token));
                }
                if (IsRepository) await EnsureSetupAsync();
                break;
            case SetupStep.Provider: OpenSetup(SetupStep.Destination); break;
            case SetupStep.Destination:
                try { ValidateSetupDestination(); OpenSetup(SetupStep.Authentication); }
                catch (Exception ex) { SetStatus(SafeError(ex)); }
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
        if (!saved || !IsRepository) return;
        if (SelectedRemote is { } selected && !await SaveAgentConnectionSelectionAsync(root, selected))
            saveDetail += "\nThe Git connection was saved, but its active selection for agents could not be saved. See Activity and select this connection again before reporting changes.";
        _completionMessage = $"{ProviderLabel} · {name}\n{url}\n\nAuthenticated repository read access verified. Git saved this destination for fetching and pushing. Existing local files and history are unchanged.\n\n{saveDetail}";
        OpenSetup(SetupStep.Complete);
    }
    private void LocalWorkspace_Click(object sender, RoutedEventArgs e) => ShowLocalWorkspace();
    private void ShowLocalWorkspace()
    {
        if (_busy || !IsRepository) return;
        _setupOpen = false;
        WorkspaceTabs.SelectedItem = WorkTab;
        SetStatus(_remoteDraftDirty ? "Connection draft kept for this session. Choose Config to continue setup." : "Git workspace ready. Choose Config to manage connections.");
        Changed();
    }
}
