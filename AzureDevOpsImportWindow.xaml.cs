using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class AzureDevOpsImportWindow : Window, IDashboardReplacementWindow
{
    public bool IsDashboardReplacementReady => !_cloning && !_checkingExit && !_confirmingClose && _unconfirmedCommands.Count == 0;
    public bool IsDashboardReplacementClosePending => _closeRequested && !_closed &&
        (_cloning || _checkingExit || _lookups.Count > 0) && _unconfirmedCommands.Count == 0;

    private const string DefaultFeatureBranch = "feature/DefaultNameReplaceMe";
    private enum ImportPage { Connection, Workspace, Repository, Branches, Review }
    private readonly string _settingsDirectory;
    private readonly IReadOnlyList<ProjectProfile> _savedProjects;
    private readonly bool _useTokenAuthentication;
    private readonly string? _existingWorkspaceFolder;
    private readonly ObservableCollection<AzureDevOpsWorkspaceRepository> _selectedRepositories = [];
    private readonly HashSet<CancellationTokenSource> _lookups = [];
    private readonly HashSet<GitCommandExitUnconfirmedException> _unconfirmedCommands = [];
    private IReadOnlyList<AzureDevOpsImportRepository> _repositories = [];
    private CancellationTokenSource? _cloneCancellation;
    private string? _loadedProjectUrl;
    private string? _branchRepositoryId;
    private string? _draftRepositoryId;
    private string? _draftStartingBranch;
    private string _draftFeatureBranch = DefaultFeatureBranch;
    private GitWorkspaceCloneReview? _cloneReview;
    private ImportPage _page = ImportPage.Connection;
    private int _selectionVersion;
    private bool _configured;
    private bool _loadingPreferences = true;
    private bool _updatingSelections;
    private bool _workspaceNameEdited;
    private bool _cloning;
    private bool _checkingExit;
    private bool _confirmingClose;
    private bool _closeRequested;
    private bool _allowClose;
    private bool _closed;
    private bool? _compactConnectionLayout;

    public string? ImportedRoot { get; private set; }
    public string? ImportedWorkspaceName { get; private set; }
    public IReadOnlyList<string> ImportedRepositories { get; private set; } = [];

    public AzureDevOpsImportWindow(string settingsDirectory, IReadOnlyList<ProjectProfile> savedProjects,
        bool useTokenAuthentication = false, string? existingWorkspaceFolder = null)
    {
        FullStackLauncher.Services.WindowTheme.Attach(this);
        _settingsDirectory = Path.GetFullPath(settingsDirectory);
        _savedProjects = savedProjects;
        _useTokenAuthentication = useTokenAuthentication;
        _existingWorkspaceFolder = existingWorkspaceFolder is null ? null : Path.GetFullPath(existingWorkspaceFolder, _settingsDirectory);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        _newWorkspaceParent = Directory.Exists(desktop) ? desktop : _settingsDirectory;
        _selectedExistingWorkspace = _existingWorkspaceFolder ?? "";
        InitializeComponent();
        if (_existingWorkspaceFolder is not null) DestinationModeBox.SelectedIndex = 1;
        RestoreDestinationFolder();
        if (_useTokenAuthentication) Title = "Connect with Azure DevOps Token";
        SelectedRepositoriesList.ItemsSource = _repositoryRows;
        _selectedRepositories.CollectionChanged += (_, _) => SynchronizeRepositoryRows();
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MaxHeight = SystemParameters.WorkArea.Height;
        Loaded += async (_, _) => await InitializeConnectionAsync();
        Closed += (_, _) => { _closed = true; ClearTokenDraft(); };
        UpdateControls();
    }

    private async Task InitializeConnectionAsync()
    {
        string? savedUrl = null;
        await RunLookupAsync(token => Task.Run(() => _useTokenAuthentication
            ? AzureDevOpsTokenStore.LoadLastProjectUrl() : AzureDevOpsImportPreferences.LoadLastProjectUrl(), token), url =>
        {
            savedUrl = string.IsNullOrWhiteSpace(url) ? null : url;
            if (savedUrl is null) return;
            SetProjectUrl(savedUrl);
            _configured = true;
            _page = ImportPage.Workspace;
        }, _selectionVersion, "Loading connection settings…");
        _loadingPreferences = false;
        UpdateControls();
        if (_closeRequested || _closed) return;
        if (savedUrl is not null) await LoadRepositoriesAsync(interactive: false);
        else
        {
            if (ErrorDetailsText.Visibility != Visibility.Visible) SetStatus("");
            InlineProjectUrlBox.Focus();
        }
    }

    private void SetProjectUrl(string url)
    {
        _updatingSelections = true;
        try { ProjectUrlBox.Text = InlineProjectUrlBox.Text = url; }
        finally { _updatingSelections = false; }
    }

    private void ProjectUrl_Changed(object sender, TextChangedEventArgs e)
    {
        if (CloneButton is null || _updatingSelections || sender is not TextBox box) return;
        SetProjectUrl(box.Text);
        InvalidateReview();
        _loadedProjectUrl = null;
        _repositories = [];
        ClearRepositorySearch();
        _draftRepositoryId = null;
        _draftStartingBranch = null;
        _draftFeatureBranch = DefaultFeatureBranch;
        ResetBranchChoices();
        SourceProjectText.Text = "";
        _updatingSelections = true;
        try { RepositoryBox.ItemsSource = null; }
        finally { _updatingSelections = false; }
        if (_page == ImportPage.Branches) _page = ImportPage.Repository;
        ClearError();
        SetStatus("Check the project connection.");
        UpdateControls();
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await LoadRepositoriesAsync(interactive: false);
    private async void SignIn_Click(object sender, RoutedEventArgs e) => await LoadRepositoriesAsync(interactive: true);

    private void Token_Changed(object sender, RoutedEventArgs e)
    {
        if (CloneButton is null || _updatingSelections || sender is not PasswordBox box) return;
        _updatingSelections = true;
        try
        {
            if (ReferenceEquals(box, InlineTokenBox)) TokenSettingsBox.Password = box.Password;
            else InlineTokenBox.Password = box.Password;
        }
        finally { _updatingSelections = false; }
        InvalidateReview();
        _loadedProjectUrl = null;
        _repositories = [];
        ClearRepositorySearch();
        ResetBranchChoices();
        ClearError();
        SetStatus("Connect to verify and save this token.");
        UpdateControls();
    }

    private void ClearTokenDraft()
    {
        _updatingSelections = true;
        try { InlineTokenBox.Clear(); TokenSettingsBox.Clear(); }
        finally { _updatingSelections = false; }
    }

    private async void ForgetToken_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNavigate || !_useTokenAuthentication) return;
        var projectUrl = ProjectUrlBox.Text.Trim();
        InvalidateReview();
        await RunLookupAsync(token => Task.Run(() => { AzureDevOpsTokenStore.ForgetToken(projectUrl); return true; }, token), _ =>
        {
            ClearTokenDraft();
            _loadedProjectUrl = null;
            _repositories = [];
            ClearRepositorySearch();
            ResetBranchChoices();
            SetStatus("Saved token removed for this organization or server collection. Enter a token to connect again.");
        }, _selectionVersion, "Removing saved token…");
    }

    private Task<IReadOnlyList<AzureDevOpsImportRepository>> ReadRepositoriesAsync(string projectUrl,
        bool interactive, CancellationToken token, string? personalAccessToken = null) => _useTokenAuthentication
        ? AzureDevOpsImportService.ListRepositoriesWithTokenAsync(projectUrl, personalAccessToken, token)
        : AzureDevOpsImportService.ListRepositoriesAsync(projectUrl, interactive, token);

    private Task<IReadOnlyList<string>> ReadBranchesAsync(string projectUrl, string repositoryId, CancellationToken token) => _useTokenAuthentication
        ? AzureDevOpsImportService.ListBranchesWithTokenAsync(projectUrl, repositoryId, token)
        : AzureDevOpsImportService.ListBranchesAsync(projectUrl, repositoryId, interactive: false, token);

    private async Task LoadRepositoriesAsync(bool interactive)
    {
        if (!CanNavigate) return;
        ClearError();
        string projectUrl;
        try { projectUrl = AzureDevOpsImportService.ParseProjectUrl(ProjectUrlBox.Text.Trim()).ProjectUrl; }
        catch (Exception exception)
        {
            ReportError("Check the Azure DevOps project URL.", exception);
            UpdateControls();
            return;
        }

        InvalidateReview();
        _loadedProjectUrl = null;
        ResetBranchChoices();
        var version = _selectionVersion;
        var connected = false;
        var tokenDraft = _useTokenAuthentication ? InlineTokenBox.Password : null;
        await RunLookupAsync(token => ReadRepositoriesAsync(projectUrl, interactive, token, tokenDraft),
            async repositories =>
            {
                connected = true;
                _loadedProjectUrl = projectUrl;
                _repositories = repositories;
                _configured = true;
                ClearTokenDraft();
                SetProjectUrl(projectUrl);
                Exception? saveError = null;
                try { await Task.Run(() =>
                {
                    if (_useTokenAuthentication) AzureDevOpsTokenStore.SaveLastProjectUrl(projectUrl);
                    else AzureDevOpsImportPreferences.SaveLastProjectUrl(projectUrl);
                }); }
                catch (Exception exception) { saveError = exception; }
                Exception? favoritesError = null;
                try { _recentRepositoryIds = await Task.Run(() => AzureDevOpsImportPreferences.LoadRecentRepositoryIds(projectUrl)); }
                catch (Exception exception) { _recentRepositoryIds = []; favoritesError = exception; }
                if (_closed || _closeRequested) return;
                if (_page == ImportPage.Connection || _page == ImportPage.Branches) _page = ImportPage.Repository;
                PopulateRepositoryChoices();
                ConfigPopup.IsOpen = false;
                if (ErrorDetailsText.Visibility != Visibility.Visible)
                    SetStatus(_page == ImportPage.Workspace ? "" : RepositoryChoicesStatus());
                if (saveError is not null)
                    ReportError("Connected, but the project URL could not be remembered.", saveError);
                if (favoritesError is not null)
                    ReportError("Connected, but recent repository favorites could not be loaded.", favoritesError);
                UpdateControls();
            }, version, interactive && !_useTokenAuthentication && AzureDevOpsImportService.ParseProjectUrl(projectUrl).IsHosted
                ? "Waiting for Microsoft sign-in and loading repositories…" : "Loading repositories…");

        if (!_closed && !_closeRequested && CanStartAction)
        {
            if (!connected && _configured) OpenSettings();
            else if (_page == ImportPage.Repository) RepositoryBox.Focus();
            else if (_page == ImportPage.Workspace) StartRepositoryButton.Focus();
        }
    }

    private void PopulateRepositoryChoices()
    {
        var keepId = _draftRepositoryId;
        var choices = AvailableRepositories().ToArray();
        var restored = choices.FirstOrDefault(repository => repository.Id == keepId);
        if (restored is null && choices.Length == 1 && string.IsNullOrEmpty(_repositorySearch)) restored = choices[0];
        if (restored is not null) _repositorySearch = restored.Name;
        _updatingSelections = true;
        try
        {
            RepositoryBox.ItemsSource = restored is null ? FilterRepositories(_repositorySearch) : choices;
            RepositoryBox.SelectedItem = restored;
            RepositoryBox.Text = _repositorySearch;
        }
        finally { _updatingSelections = false; }
        PopulateRecentRepositories();
        SourceProjectText.Text = _loadedProjectUrl is null ? "" : AzureDevOpsImportService.ParseProjectUrl(_loadedProjectUrl).Project;
        _draftRepositoryId = restored?.Id;
    }

    private async void StartRepository_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNavigate || !_configured || _selectedRepositories.Count >= 128) return;
        BeginRepositoryFlow();
        if (_loadedProjectUrl is null) await LoadRepositoriesAsync(interactive: false);
        else
        {
            PopulateRepositoryChoices();
            SetStatus(RepositoryChoicesStatus());
            UpdateControls();
            RepositoryBox.Focus();
        }
    }

    private void BeginRepositoryFlow()
    {
        InvalidateReview();
        ClearError();
        _draftRepositoryId = null;
        _repositorySearch = "";
        _draftStartingBranch = null;
        _draftFeatureBranch = DefaultFeatureBranch;
        ResetBranchChoices();
        ShowPage(ImportPage.Repository);
    }

    private string RepositoryChoicesStatus() => _repositories.Count == 0 ? "No accessible repositories were found in this project."
        : !AvailableRepositories().Any() ? "All repositories from this project are already added."
        : RepositoryBox.Items.Count == 0 ? "No matching repositories. Change or clear the search."
        : RepositoryBox.SelectedItem is null ? "Choose a repository." : "";

    private void Repository_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections || CloneButton is null) return;
        InvalidateReview();
        ClearError();
        var repository = RepositoryBox.SelectedItem as AzureDevOpsImportRepository;
        if (repository is not null) _repositorySearch = repository.Name;
        _draftRepositoryId = repository?.Id;
        _draftStartingBranch = null;
        _draftFeatureBranch = DefaultFeatureBranch;
        ResetBranchChoices();
        SetStatus(repository is null ? "Choose a repository." : "");
        UpdateControls();
    }

    private async void Next_Click(object sender, RoutedEventArgs e) => await ShowBranchFlowAsync();

    private async Task ShowBranchFlowAsync()
    {
        if (!CanNavigate || RepositoryBox.SelectedItem is not AzureDevOpsImportRepository repository || _loadedProjectUrl is null) return;
        ShowPage(ImportPage.Branches);
        BranchRepositoryText.Text = AzureDevOpsImportService.ParseProjectUrl(_loadedProjectUrl).Project + " / " + repository.Name;
        if (_branchRepositoryId == repository.Id)
        {
            UpdateControls();
            BranchBox.Focus();
            return;
        }
        var projectUrl = _loadedProjectUrl;
        var version = _selectionVersion;
        await RunLookupAsync(token => ReadBranchesAsync(projectUrl, repository.Id, token),
            async branches =>
            {
                _branchRepositoryId = repository.Id;
                _updatingSelections = true;
                try
                {
                    BranchBox.ItemsSource = branches;
                    var startingBranch = _draftStartingBranch ?? ShortBranch(repository.DefaultBranch);
                    if (branches.Contains(startingBranch, StringComparer.Ordinal)) BranchBox.SelectedItem = startingBranch;
                    _draftStartingBranch = BranchBox.SelectedItem as string;
                    FeatureBranchBox.Text = _draftFeatureBranch;
                }
                finally { _updatingSelections = false; }
                SetStatus(branches.Count == 0 ? "This repository has no branches to clone."
                    : BranchBox.SelectedItem is null ? "Select a starting branch." : "");
                UpdateControls();
                if (branches.Count > 0) await RememberRepositoryAsync(projectUrl, repository.Id, version);
            }, version, "Loading branches…");
        if (CanStartAction) BranchBox.Focus();
    }

    private void Branch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections || CloneButton is null) return;
        _draftStartingBranch = BranchBox.SelectedItem as string;
        ChangeDraft();
    }

    private void FeatureBranch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updatingSelections || CloneButton is null) return;
        _draftFeatureBranch = FeatureBranchBox.Text;
        ChangeDraft();
    }

    private void WorkspaceName_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updatingSelections || CloneButton is null) return;
        _workspaceNameEdited = true;
        InvalidateReview(keepReviewPage: true);
        ClearError();
        SetStatus("");
        UpdateControls();
    }

    private bool UsesExistingWorkspace => _existingWorkspaceFolder is not null || DestinationModeBox.SelectedIndex == 1;

    private void DestinationMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections || CloneButton is null) return;
        RestoreDestinationFolder();
        ChangeDraft();
    }

    private void ChangeDraft()
    {
        if (_updatingSelections || CloneButton is null) return;
        InvalidateReview();
        ClearError();
        SetStatus("");
        UpdateControls();
    }

    private void AddRepository_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNavigate || !TryGetSelection(out var selection)) return;
        if (!IsFeatureBranchName(selection.FeatureBranch))
        {
            ReportError("Enter a valid feature branch.", new InvalidOperationException("Use a Git branch name such as feature/my-change."));
            FeatureBranchBox.Focus();
            return;
        }
        if (BranchBox.Items.Cast<string>().Any(branch => BranchNamesConflict(branch, selection.FeatureBranch)))
        {
            ReportError("Choose a new feature branch.", new InvalidOperationException("The feature branch conflicts with an existing repository branch."));
            return;
        }
        if (_selectedRepositories.Any(item => SameRepository(item.Repository, selection.Repository)))
        {
            ReportError("This repository is already added.", new InvalidOperationException("Edit its branch choices from the workspace."));
            return;
        }
        if (_selectedRepositories.Count >= 128)
        {
            ReportError("Workspace repository limit reached.", new InvalidOperationException("Choose up to 128 repositories per workspace."));
            return;
        }
        selection = selection with { LocalFolderName = ChooseRepositoryFolderName(selection) };
        InvalidateReview();
        ClearError();
        _selectedRepositories.Add(selection);
        EndRepositoryFlow();
        StartRepositoryButton.Focus();
    }

    private void RemoveRepository_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNavigate || sender is not Button { DataContext: AzureDevOpsRepositoryRow row }) return;
        var selection = row.Selection;
        InvalidateReview();
        ClearError();
        _selectedRepositories.Remove(selection);
        SetStatus("");
        UpdateControls();
        StartRepositoryButton.Focus();
    }

    private void EndRepositoryFlow()
    {
        InvalidateReview();
        _draftRepositoryId = null;
        _draftStartingBranch = null;
        _draftFeatureBranch = DefaultFeatureBranch;
        ResetBranchChoices();
        ShowPage(ImportPage.Workspace);
        SetStatus("");
        UpdateControls();
    }

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNavigate || !CanReviewWorkspace) return;
        if (UsesExistingWorkspace)
        {
            await PrepareWorkspaceReviewAsync();
            if (_cloneReview is not null && CanStartAction) CloneButton.Focus();
            return;
        }
        ClearError();
        _cloneReview = null;
        ReviewRepositoriesList.ItemsSource = _selectedRepositories.ToArray();
        ShowPage(ImportPage.Review);
        if (!_workspaceNameEdited)
        {
            string parent;
            string[] savedFolders;
            try
            {
                parent = ResolveDestinationFolder();
                savedFolders = _savedProjects.Select(project => Path.GetFullPath(project.RootPath, _settingsDirectory)).ToArray();
            }
            catch (Exception exception)
            {
                ReportError("Check the workspace destination.", exception);
                return;
            }
            var version = _selectionVersion;
            var suggested = false;
            await RunLookupAsync(token => Task.Run(() => WorkspaceNameSuggestion.FindAvailable(parent, savedFolders, token), token), name =>
            {
                _updatingSelections = true;
                try { WorkspaceNameBox.Text = name; }
                finally { _updatingSelections = false; }
                suggested = true;
            }, version, "Checking existing workspace names…");
            if (!CanStartAction || version != _selectionVersion) return;
            if (!suggested)
            {
                WorkspaceNameBox.Focus();
                WorkspaceNameBox.SelectAll();
                return;
            }
        }
        SetStatus("");
        WorkspaceNameBox.Focus();
        WorkspaceNameBox.SelectAll();
    }

    private async Task PrepareWorkspaceReviewAsync()
    {
        if (!CanStartAction || !CanReviewWorkspace) return;
        var chooseExistingFolders = UsesExistingWorkspace && _page == ImportPage.Workspace;
        InvalidateReview(keepReviewPage: true);
        var existing = UsesExistingWorkspace;
        ClearError();
        var workspaceName = WorkspaceNameBox.Text.Trim();
        string parentFolder;
        try
        {
            parentFolder = ResolveDestinationFolder();
            RequireUnusedDestination(existing ? parentFolder : Path.Combine(parentFolder, workspaceName));
        }
        catch (Exception exception)
        {
            ReportError("Check the workspace destination.", exception);
            return;
        }
        var version = _selectionVersion;
        var selections = _selectedRepositories.ToArray();
        await RunLookupAsync(async token => existing
            ? await GitRepositoryService.PrepareExistingWorkspaceCloneAsync(parentFolder,
                chooseExistingFolders
                    ? await Task.Run(() => ChooseAvailableRepositoryFolders(parentFolder, selections), token)
                    : selections, token)
            : await GitRepositoryService.PrepareWorkspaceCloneAsync(parentFolder, workspaceName, selections, token),
            review =>
            {
                RequireUnusedDestination(review.Folder, review.Repositories.Select(item => item.Folder));
                for (var index = 0; index < review.Repositories.Count; index++)
                    if (_selectedRepositories[index] != review.Repositories[index].Selection)
                        _selectedRepositories[index] = review.Repositories[index].Selection;
                _cloneReview = review;
                ReviewRepositoriesList.ItemsSource = review.Repositories;
                ShowPage(ImportPage.Review);
                SetStatus("");
            }, version, "Checking the workspace destination and branches…");
    }

    private static AzureDevOpsWorkspaceRepository[] ChooseAvailableRepositoryFolders(string workspace,
        IReadOnlyList<AzureDevOpsWorkspaceRepository> selections)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return selections.Select(selection =>
        {
            var stem = SafeFolderName(string.IsNullOrEmpty(selection.LocalFolderName)
                ? selection.Repository.Name : selection.LocalFolderName);
            var name = stem;
            for (var suffix = 2; names.Contains(name) || DestinationEntryExists(Path.Combine(workspace, name)); suffix++)
                name = stem + "-" + suffix;
            names.Add(name);
            return selection with { LocalFolderName = name };
        }).ToArray();
    }

    private static bool DestinationEntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private async void Clone_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartAction || _page != ImportPage.Review || !CanReviewWorkspace
            || (!UsesExistingWorkspace && string.IsNullOrWhiteSpace(WorkspaceNameBox.Text))) return;
        await PrepareWorkspaceReviewAsync();
        if (!CanStartAction || _page != ImportPage.Review || _cloneReview is not { } review || !ReviewMatchesSelection(review)) return;
        try { RequireUnusedDestination(review.Folder, review.Repositories.Select(item => item.Folder)); }
        catch (Exception exception)
        {
            InvalidateReview(keepReviewPage: true);
            ReportError("The destination needs review again.", exception);
            UpdateControls();
            return;
        }

        ClearError();
        _cloning = true;
        _cloneCancellation = new CancellationTokenSource();
        UpdateControls();
        SetStatus("Cloning repositories…");
        var succeeded = false;
        string? cloningStatus = null;
        try
        {
            var progress = new Progress<string>(message =>
            {
                cloningStatus = message;
                if (!_closed && _cloning && !_closeRequested && _unconfirmedCommands.Count == 0) SetStatus(message);
            });
            var result = await GitRepositoryService.CloneWorkspaceAsync(review, progress, _cloneCancellation.Token);
            succeeded = true;
            SetStatus(result);
            if (!_closeRequested)
            {
                if (result.Contains("not confirmed", StringComparison.OrdinalIgnoreCase))
                    MessageBox.Show(this, SensitiveDataProtection.Redact(result), "Clone completed with a warning",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                ImportedRoot = review.Folder;
                ImportedWorkspaceName = review.WorkspaceName;
                ImportedRepositories = review.Repositories.Select(item => item.Folder).ToArray();
            }
        }
        catch (GitCommandExitUnconfirmedException exception)
        {
            TrackUnconfirmed(exception);
            ErrorDetailsText.Text = SensitiveDataProtection.Redact(review.Folder + "\n" + cloningStatus) + "\n" + ErrorDetailsText.Text;
        }
        catch (OperationCanceledException exception)
        {
            ReportError("Clone canceled or timed out. Downloaded files remain. Inspect the workspace before retrying.", exception);
        }
        catch (Exception exception)
        {
            ReportError("Clone did not complete. Downloaded files remain. Inspect the workspace before retrying.", exception);
        }
        finally
        {
            _cloneCancellation.Dispose();
            _cloneCancellation = null;
            _cloning = false;
            _cloneReview = null;
            if (!succeeded && !_closeRequested) ShowPage(ImportPage.Review);
            UpdateControls();
        }

        if (succeeded && !_closeRequested)
        {
            _allowClose = true;
            DialogResult = true;
        }
        else FinishDeferredClose();
    }

    private Task RunLookupAsync<T>(Func<CancellationToken, Task<T>> action, Action<T> apply, int version, string status) =>
        RunLookupAsync(action, result => { apply(result); return Task.CompletedTask; }, version, status);

    private async Task RunLookupAsync<T>(Func<CancellationToken, Task<T>> action, Func<T, Task> apply, int version, string status)
    {
        var cancellation = new CancellationTokenSource();
        _lookups.Add(cancellation);
        SetStatus(status);
        UpdateControls();
        try
        {
            var result = await action(cancellation.Token);
            if (!IsCurrent(version, cancellation)) return;
            await apply(result);
        }
        catch (GitCommandExitUnconfirmedException exception) { TrackUnconfirmed(exception); }
        catch (OperationCanceledException exception)
        {
            if (IsCurrent(version, cancellation, allowCanceled: true) && !_closeRequested)
                ReportError(_useTokenAuthentication ? "Operation canceled or timed out. Connect again."
                    : "Operation canceled or timed out. Check again, or use Sign in & load.", exception);
        }
        catch (Exception exception)
        {
            if (IsCurrent(version, cancellation)) ReportError("Could not complete this check. Review the details.", exception);
        }
        finally
        {
            _lookups.Remove(cancellation);
            cancellation.Dispose();
            UpdateControls();
            FinishDeferredClose();
        }
    }

    private bool IsCurrent(int version, CancellationTokenSource cancellation, bool allowCanceled = false) =>
        !_closed && !_closeRequested && version == _selectionVersion && (allowCanceled || !cancellation.IsCancellationRequested);

    private bool CanStartAction => !_cloning && !_checkingExit && !_confirmingClose && !_closeRequested && !_closed
        && _unconfirmedCommands.Count == 0 && _lookups.Count == 0;

    private bool TryGetSelection(out AzureDevOpsWorkspaceRepository selection)
    {
        selection = null!;
        if (_page != ImportPage.Branches || RepositoryBox.SelectedItem is not AzureDevOpsImportRepository repository
            || BranchBox.SelectedItem is not string branch || string.IsNullOrWhiteSpace(branch)
            || string.IsNullOrWhiteSpace(FeatureBranchBox.Text)
            || _branchRepositoryId != repository.Id || _loadedProjectUrl is null) return false;
        string projectUrl;
        try { projectUrl = AzureDevOpsImportService.ParseProjectUrl(ProjectUrlBox.Text.Trim()).ProjectUrl; }
        catch { return false; }
        if (projectUrl != _loadedProjectUrl) return false;
        selection = new AzureDevOpsWorkspaceRepository
        {
            Repository = repository, StartingBranch = branch, FeatureBranch = FeatureBranchBox.Text.Trim(),
            ProjectUrl = projectUrl, ProjectName = AzureDevOpsImportService.ParseProjectUrl(projectUrl).Project,
            UseAzureDevOpsToken = _useTokenAuthentication
        };
        return true;
    }

    private bool CanReviewWorkspace => _inlineRow is null && _selectedRepositories.Count > 0
        && (!_useTokenAuthentication || InlineTokenBox.Password.Length == 0)
        && !string.IsNullOrWhiteSpace(DestinationFolderBox.Text)
        && _page is ImportPage.Workspace or ImportPage.Review;

    private bool ReviewMatchesSelection(GitWorkspaceCloneReview review) => CanReviewWorkspace
        && review.UsesExistingWorkspace == UsesExistingWorkspace
        && DestinationMatchesReview(review)
        && (review.UsesExistingWorkspace || string.Equals(WorkspaceNameBox.Text.Trim(), review.WorkspaceName, StringComparison.Ordinal))
        && _selectedRepositories.SequenceEqual(review.Repositories.Select(item => item.Selection));

    private static bool SameRepository(AzureDevOpsImportRepository first, AzureDevOpsImportRepository second) =>
        string.Equals(first.CloneUrl, second.CloneUrl, StringComparison.OrdinalIgnoreCase);

    private static bool BranchNamesConflict(string first, string second) => first.Equals(second, StringComparison.OrdinalIgnoreCase)
        || first.StartsWith(second + "/", StringComparison.OrdinalIgnoreCase)
        || second.StartsWith(first + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsFeatureBranchName(string branch) => branch.Length is > 0 and <= 250 && !branch.StartsWith('-')
        && !branch.StartsWith('@') && !branch.EndsWith('.') && !branch.Any(char.IsControl)
        && branch.IndexOfAny([' ', '~', '^', ':', '?', '*', '[', '\\']) < 0 && !branch.Contains("..", StringComparison.Ordinal)
        && !branch.Contains("@{", StringComparison.Ordinal) && branch.Split('/').All(part => part.Length > 0
            && !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));

    private string ChooseRepositoryFolderName(AzureDevOpsWorkspaceRepository selection, AzureDevOpsWorkspaceRepository? previous = null)
    {
        var usedNames = _selectedRepositories.Where(item => item != previous)
            .Select(item => string.IsNullOrEmpty(item.LocalFolderName) ? item.Repository.Name : item.LocalFolderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preferred = previous is not null && SameRepository(previous.Repository, selection.Repository) ? previous.LocalFolderName : null;
        if (string.IsNullOrWhiteSpace(preferred)) preferred = SafeFolderName(selection.Repository.Name);
        if (!usedNames.Contains(preferred)) return preferred;
        var stem = SafeFolderName(selection.ProjectName + "-" + selection.Repository.Name);
        var folder = stem;
        for (var suffix = 2; usedNames.Contains(folder); suffix++) folder = stem + "-" + suffix;
        return folder;
    }

    private static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(character => char.IsControl(character) || invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > 200) clean = clean[..200].TrimEnd('.', ' ');
        if (clean.Length == 0) return "repository";
        var stem = clean.Split('.')[0].TrimEnd();
        if (clean.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$" }.Contains(stem, StringComparer.OrdinalIgnoreCase)
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3]))
            clean = "_" + clean;
        return clean;
    }

    private void RequireUnusedDestination(string folder, IEnumerable<string>? repositoryFolders = null)
    {
        foreach (var project in _savedProjects)
        {
            var root = Path.GetFullPath(project.RootPath, _settingsDirectory);
            var selectedProject = _existingWorkspaceFolder is not null
                && string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(_existingWorkspaceFolder), StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
            if (!selectedProject && SettingsStore.WorkingFoldersOverlap(folder, root))
                throw new InvalidOperationException($"This destination overlaps saved project '{project.Name}'. Use that project's Functions → Clone repositories action, or choose a separate workspace.");
            foreach (var service in project.Services)
            {
                var working = Path.GetFullPath(service.WorkingDirectory, root);
                if ((!selectedProject && SettingsStore.WorkingFoldersOverlap(folder, working))
                    || (selectedProject && repositoryFolders is not null && repositoryFolders.Any(child => SettingsStore.WorkingFoldersOverlap(child, working))))
                    throw new InvalidOperationException($"This destination overlaps '{project.Name}' / '{service.Name}'. Choose a separate workspace.");
            }
        }
    }

    private void InvalidateReview(bool keepReviewPage = false)
    {
        _selectionVersion++;
        CancelLookups();
        _cloneReview = null;
        if (!keepReviewPage && _page == ImportPage.Review) _page = ImportPage.Workspace;
    }

    private void ResetBranchChoices()
    {
        _branchRepositoryId = null;
        _updatingSelections = true;
        try { BranchBox.ItemsSource = null; FeatureBranchBox.Text = _draftFeatureBranch; }
        finally { _updatingSelections = false; }
    }

    private void CancelLookups()
    {
        foreach (var cancellation in _lookups) cancellation.Cancel();
    }

    private void ShowPage(ImportPage page)
    {
        _page = page;
        UpdateControls();
        PageScrollViewer.ScrollToTop();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartAction) return;
        ClearError();
        if (_page == ImportPage.Branches)
        {
            ShowPage(ImportPage.Repository);
            SetStatus("");
            RepositoryBox.Focus();
        }
        else if (_page == ImportPage.Repository)
        {
            EndRepositoryFlow();
            StartRepositoryButton.Focus();
        }
        else
        {
            InvalidateReview();
            ShowPage(ImportPage.Workspace);
            SetStatus("");
            StartRepositoryButton.Focus();
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNavigate || _page == ImportPage.Review) return;
        if (ConfigPopup.IsOpen) ConfigPopup.IsOpen = false;
        else OpenSettings();
    }

    private void OpenSettings()
    {
        ConfigBorder.Width = Math.Min(420, Math.Max(280, ActualWidth - 44));
        ConfigPopup.IsOpen = true;
    }

    private void ConfigPopup_Opened(object sender, EventArgs e) => ProjectUrlBox.Focus();

    private void Config_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ConfigPopup.IsOpen = false;
        e.Handled = true;
    }

    private void ConfigPopup_Closed(object sender, EventArgs e)
    {
        if (!_closed && CanStartAction) SettingsButton.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (RepositoryBox.IsDropDownOpen)
        {
            RepositoryBox.IsDropDownOpen = false;
            e.Handled = true;
        }
        else if (ConfigPopup.IsOpen)
        {
            ConfigPopup.IsOpen = false;
            e.Handled = true;
        }
        else if (_inlineRow is not null)
        {
            if (!CloseInlineDropdown()) CancelInlineEdit();
            e.Handled = true;
        }
        else if (CanStartAction && _page is ImportPage.Repository or ImportPage.Branches)
        {
            ClearError();
            EndRepositoryFlow();
            StartRepositoryButton.Focus();
            e.Handled = true;
        }
        else
        {
            if (CancelButton.IsEnabled) Close();
            e.Handled = true;
        }
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        CancelLookups();
        _cloneCancellation?.Cancel();
        SetStatus(_cloning ? "Cancel requested. Waiting for Git to stop; downloaded files remain."
            : "Cancel requested. Waiting for the check to stop…");
    }

    private void TrackUnconfirmed(GitCommandExitUnconfirmedException exception)
    {
        _unconfirmedCommands.Add(exception);
        CancelLookups();
        _cloneReview = null;
        ConfigPopup.IsOpen = false;
        ReportError("Git process exit is not confirmed. Use Check process exit before continuing or closing. Downloaded files remain.", exception);
        UpdateControls();
    }

    private async void CheckExit_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingExit || _cloning || _lookups.Count > 0) return;
        _checkingExit = true;
        UpdateControls();
        try
        {
            var commands = _unconfirmedCommands.ToArray();
            var confirmed = await Task.Run(() => commands.Where(command => command.IsExitConfirmed()).ToArray());
            foreach (var command in confirmed) _unconfirmedCommands.Remove(command);
            SetStatus(_unconfirmedCommands.Count == 0
                ? "Git process exit confirmed. Inspect downloaded files before another clone attempt."
                : "Git process exit remains unconfirmed. Wait, then check process exit again.");
        }
        finally
        {
            _checkingExit = false;
            UpdateControls();
            FinishDeferredClose();
        }
    }

    private void UpdateWindowSize()
    {
        var compact = _page == ImportPage.Connection;
        if (_compactConnectionLayout == compact) return;
        _compactConnectionLayout = compact;
        var area = SystemParameters.WorkArea;
        var centerX = Left + ActualWidth / 2;
        var centerY = Top + ActualHeight / 2;
        MinHeight = Math.Min(compact ? 260 : 420, area.Height);
        Width = Math.Min(compact ? 600 : 760, area.Width);
        if (compact)
        {
            SizeToContent = SizeToContent.Height;
            return;
        }
        SizeToContent = SizeToContent.Manual;
        Height = Math.Min(690, area.Height);
        if (IsLoaded && WindowState == WindowState.Normal)
        {
            Left = Math.Clamp(centerX - Width / 2, area.Left, area.Right - Width);
            Top = Math.Clamp(centerY - Height / 2, area.Top, area.Bottom - Height);
        }
    }

    private void UpdateControls()
    {
        if (_closed || CloneButton is null) return;
        UpdateWindowSize();
        var busy = _cloning || _checkingExit || _lookups.Count > 0;
        var ready = CanStartAction && !_loadingPreferences;
        var navigate = ready && _inlineRow is null;
        UpdateInlineControls(ready);
        ConnectionPage.Visibility = !_loadingPreferences && _page == ImportPage.Connection ? Visibility.Visible : Visibility.Collapsed;
        WorkspacePage.Visibility = !_loadingPreferences && _page == ImportPage.Workspace ? Visibility.Visible : Visibility.Collapsed;
        RepositoryPage.Visibility = _page == ImportPage.Repository ? Visibility.Visible : Visibility.Collapsed;
        BranchesPage.Visibility = _page == ImportPage.Branches ? Visibility.Visible : Visibility.Collapsed;
        ReviewPage.Visibility = _page == ImportPage.Review ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = _page switch
        {
            ImportPage.Connection => _useTokenAuthentication ? "Connect with Azure DevOps Token" : "Connect Azure DevOps",
            ImportPage.Repository => "Add repository", ImportPage.Branches => "Choose branches",
            ImportPage.Review => UsesExistingWorkspace ? "Add repositories to workspace" : "Clone workspace", _ => "Workspace"
        };
        SettingsButton.Visibility = _configured ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.IsEnabled = navigate && _page != ImportPage.Review;
        ProjectUrlBox.IsEnabled = InlineProjectUrlBox.IsEnabled = navigate && _page != ImportPage.Review;
        CheckButton.IsEnabled = SignInButton.IsEnabled = InlineCheckButton.IsEnabled = InlineSignInButton.IsEnabled = navigate && _page != ImportPage.Review;
        TokenSettingsPanel.Visibility = InlineTokenPanel.Visibility = _useTokenAuthentication ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.Visibility = InlineCheckButton.Visibility = _useTokenAuthentication ? Visibility.Collapsed : Visibility.Visible;
        AzureDevOpsProjectTarget? target = null;
        try { target = AzureDevOpsImportService.ParseProjectUrl(ProjectUrlBox.Text); }
        catch (InvalidOperationException) { }
        SettingsTargetText.Text = InlineTargetText.Text = target is null ? "" :
            $"Collection: {target.CollectionUrl}\nProject: {target.Project}";
        SettingsTargetText.Visibility = InlineTargetText.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        SignInButton.Content = InlineSignInButton.Content = _useTokenAuthentication ? "_Connect & save"
            : target is { IsHosted: false } ? "_Connect & load" : "_Sign in & load";
        SignInButton.ToolTip = InlineSignInButton.ToolTip = target is { IsHosted: false } && !_useTokenAuthentication
            ? "Use your current Windows account. For token authentication, choose Connect with Azure DevOps Token when creating a project."
            : null;
        TokenSettingsBox.IsEnabled = InlineTokenBox.IsEnabled = ForgetTokenButton.IsEnabled = navigate && _page != ImportPage.Review;
        DestinationModeBox.IsEnabled = navigate && _existingWorkspaceFolder is null;
        WorkspaceNamePanel.Visibility = UsesExistingWorkspace ? Visibility.Collapsed : Visibility.Visible;
        UpdateDestinationControls(navigate);
        WorkspaceNameBox.IsEnabled = navigate && _page == ImportPage.Review && !UsesExistingWorkspace;
        StartRepositoryButton.IsEnabled = navigate && _configured && _selectedRepositories.Count < 128;
        SelectedRepositoriesList.IsEnabled = !_loadingPreferences && !_cloning && !_checkingExit && !_confirmingClose && !_closeRequested
            && _unconfirmedCommands.Count == 0 && (_lookups.Count == 0 || _inlineRow is not null);
        EmptyRepositoriesText.Visibility = _selectedRepositories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceRepositoryHeaders.Visibility = _selectedRepositories.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RepositoryBox.IsEnabled = navigate && _loadedProjectUrl is not null && AvailableRepositories().Any();
        RecentRepositoriesList.IsEnabled = navigate && _loadedProjectUrl is not null;
        BranchBox.IsEnabled = ready && BranchBox.Items.Count > 0 && _branchRepositoryId is not null;
        FeatureBranchBox.IsEnabled = ready && _branchRepositoryId is not null;
        NextButton.Visibility = _page == ImportPage.Repository ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsEnabled = ready && RepositoryBox.SelectedItem is not null && _loadedProjectUrl is not null;
        AddRepositoryButton.Visibility = _page == ImportPage.Branches ? Visibility.Visible : Visibility.Collapsed;
        AddRepositoryButton.Content = "_Add repository";
        AddRepositoryButton.IsEnabled = ready && TryGetSelection(out _);
        ChooseFolderButton.Visibility = _page == ImportPage.Workspace ? Visibility.Visible : Visibility.Collapsed;
        ChooseFolderButton.IsEnabled = navigate && CanReviewWorkspace;
        ChooseFolderButton.Content = UsesExistingWorkspace ? "_Review repositories" : "_Review workspace";
        ChooseFolderButton.ToolTip = UsesExistingWorkspace ? "Review new repository folders inside this workspace." : "Review the new workspace folder and selected repository branches.";
        CloneButton.Visibility = _page == ImportPage.Review ? Visibility.Visible : Visibility.Collapsed;
        CloneButton.Content = UsesExistingWorkspace ? "_Add repositories" : "_Create workspace";
        CloneButton.IsEnabled = ready && CanReviewWorkspace
            && (UsesExistingWorkspace || !string.IsNullOrWhiteSpace(WorkspaceNameBox.Text));
        NextButton.IsDefault = _page == ImportPage.Repository;
        AddRepositoryButton.IsDefault = _page == ImportPage.Branches;
        CloneButton.IsDefault = _page == ImportPage.Review;
        BackButton.Visibility = _page is ImportPage.Repository or ImportPage.Branches or ImportPage.Review ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Content = _page == ImportPage.Repository ? "Cancel setup" : "_Back";
        BackButton.IsEnabled = ready;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.Visibility = _cloning || _lookups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.IsEnabled = !_closeRequested;
        CancelButton.IsEnabled = !_checkingExit && !_confirmingClose;
        CheckExitButton.Visibility = _unconfirmedCommands.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CheckExitButton.IsEnabled = !_checkingExit && !_cloning && _lookups.Count == 0;
    }

    private void SetStatus(string text) => StatusText.Text = SensitiveDataProtection.Redact(text);

    private void ClearError()
    {
        ErrorDetailsText.Clear();
        ErrorDetailsText.Visibility = Visibility.Collapsed;
    }

    private void ReportError(string summary, Exception exception)
    {
        SetStatus(summary);
        var detail = SensitiveDataProtection.Redact(exception.Message);
        ErrorDetailsText.Text = string.IsNullOrEmpty(ErrorDetailsText.Text) ? detail : ErrorDetailsText.Text + "\n\n" + detail;
        ErrorDetailsText.Visibility = Visibility.Visible;
    }

    private static string ShortBranch(string? branch) => branch?.StartsWith("refs/heads/", StringComparison.Ordinal) == true
        ? branch["refs/heads/".Length..] : branch ?? "";

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_confirmingClose)
        {
            e.Cancel = true;
            return;
        }
        if (_allowClose) return;
        if (_unconfirmedCommands.Count > 0 || _checkingExit)
        {
            e.Cancel = true;
            SetStatus("Git process exit must be confirmed before closing. Use Check process exit.");
            return;
        }
        if (!_closeRequested && _selectedRepositories.Count > 0)
        {
            var count = _selectedRepositories.Count;
            var repositories = count == 1 ? "repository" : "repositories";
            var confirmed = false;
            _confirmingClose = true;
            UpdateControls();
            try
            {
                confirmed = MessageBox.Show(this,
                    $"You have added {count} {repositories} to this workspace setup.\n\n" +
                    "Canceling will discard your repository selections and branch choices. " +
                    "Existing repository folders and downloaded files will remain on disk.\n\n" +
                    "Cancel workspace setup?",
                    "Cancel workspace setup", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) == MessageBoxResult.Yes;
            }
            finally
            {
                _confirmingClose = false;
                UpdateControls();
            }
            // A clone can finish while the confirmation pumps the dispatcher.
            if (_allowClose) return;
            if (!confirmed)
            {
                e.Cancel = true;
                return;
            }
            if (_unconfirmedCommands.Count > 0 || _checkingExit)
            {
                e.Cancel = true;
                SetStatus("Git process exit must be confirmed before closing. Use Check process exit.");
                return;
            }
        }
        ConfigPopup.IsOpen = false;
        _closeRequested = true;
        _selectionVersion++;
        CancelLookups();
        _cloneCancellation?.Cancel();
        if (_cloning || _lookups.Count > 0)
        {
            e.Cancel = true;
            SetStatus(_cloning ? "Cancel requested. Waiting for Git to stop; downloaded files remain."
                : "Cancel requested. Waiting for the check to stop before closing…");
            UpdateControls();
        }
    }

    private void FinishDeferredClose()
    {
        if (!_closed && _closeRequested && !_cloning && !_checkingExit && _lookups.Count == 0 && _unconfirmedCommands.Count == 0)
        {
            _allowClose = true;
            Close();
        }
    }
}
