using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using Microsoft.Win32;

namespace FullStackLauncher;

public partial class GitProjectConnectWindow : Window, INotifyPropertyChanged, IDashboardReplacementWindow
{
    private readonly Func<GitProjectConnectRequest, Task<IDisposable>>? _prepare;
    private readonly Func<GitProjectConnectResult, Task>? _apply;
    private GitProjectConnectReview? _review;
    private GitProjectConnectResult? _connected;
    private GitCommandExitUnconfirmedException? _unconfirmed;
    private CancellationTokenSource? _operation;
    private IDisposable? _mutationLease;
    private bool _ready;
    private bool _busy;
    private string _status = "";

    public GitProjectConnectWindow(string sourceFolder,
        Func<GitProjectConnectRequest, Task<IDisposable>>? prepare,
        Func<GitProjectConnectResult, Task>? apply)
    {
        FullStackLauncher.Services.WindowTheme.Attach(this);
        SourceFolder = sourceFolder;
        _prepare = prepare;
        _apply = apply;
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceFolder));
        RepositorySubfolder.Text = name;
        DestinationFolder.Text = Path.Combine(Path.GetDirectoryName(sourceFolder) ?? sourceFolder, name + "-repository");
        DataContext = this;
        _ready = true;
        Loaded += (_, _) => CloneUrl.Focus();
        Closed += (_, _) => { _mutationLease?.Dispose(); _mutationLease = null; };
    }

    public string SourceFolder { get; }
    public GitProjectConnectResult? Result { get; private set; }
    public bool RepositoryOperationAttempted { get; private set; }
    public bool IsDashboardReplacementReady => CanClose;
    public bool IsDashboardReplacementClosePending => false;
    public bool IsBusy => _busy;
    public bool CanClose => !_busy && _unconfirmed == null;
    public bool CanEdit => CanClose && _connected == null;
    public bool CanContinue => CanClose && (Result != null || _connected != null || _review != null || !string.IsNullOrWhiteSpace(CloneUrl?.Text));
    public bool CanCheckExit => !_busy && _unconfirmed != null;
    private bool CopyProject => CopyMode?.IsChecked == true;
    public Visibility EmptyVisibility => !CopyProject ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CopyVisibility => CopyProject ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FormVisibility => _review == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReviewVisibility => _review != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BackVisibility => _review != null && _connected == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BusyVisibility => _busy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExitCheckVisibility => _unconfirmed != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NextVisibility => _unconfirmed == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExclusionsVisibility => _review?.ExcludedEntries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ExclusionsText => _review == null ? "" : string.Join(Environment.NewLine, _review.ExcludedEntries);
    public string ExclusionsLabel => _review?.Request.Mode == GitProjectConnectMode.EmptyRemoteInPlace
        ? "Default local-file exclusions" : "Files kept in the original folder";
    public string ReviewText => _review is not { } review ? "" : SensitiveDataProtection.Redact(
        $"Repository URL\n{review.Request.CloneUrl}\n\nCurrent project\n{review.SourceFolder}\n\nRepository checkout\n{review.RepositoryFolder}\n\nProject folder after connection\n{review.ProjectFolder}\n\n"
        + (review.RemoteIsEmpty ? $"First branch: {review.Request.Branch}\n"
            : $"Starting branch: {review.StartingBranch}\nWorking branch: {review.Request.FeatureBranch ?? review.StartingBranch}\nReviewed commit: {review.StartingCommit}\n")
        + $"\n{review.Summary}\n\nNothing is committed or pushed by this connection.");
    public string Status => _status;
    public string NextLabel => Result != null ? "Open Git workspace" : _connected != null ? "Retry Launcher update" : _review != null ? "Connect project" : "Check & review";
    public string CloseLabel => Result != null || _connected != null ? "Close" : "Cancel";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed() => PropertyChanged?.Invoke(this, new(null));
    private void SetStatus(string message) { _status = SensitiveDataProtection.Redact(message); Changed(); }

    private void Mode_Changed(object sender, RoutedEventArgs e) { if (_ready) ResetReview(); }
    private void Draft_Changed(object sender, RoutedEventArgs e) { if (_ready) ResetReview(); }
    private void ResetReview() { _review = null; SetStatus(""); }

    private GitProjectConnectRequest CaptureRequest() => new()
    {
        SourceFolder = SourceFolder,
        Provider = ProviderPicker.SelectedIndex == 1 ? GitHostingProvider.AzureDevOps : GitHostingProvider.GitHub,
        CloneUrl = CloneUrl.Text.Trim(),
        Mode = CopyProject ? GitProjectConnectMode.CopyIntoRepository : GitProjectConnectMode.EmptyRemoteInPlace,
        DestinationFolder = CopyProject ? DestinationFolder.Text.Trim() : null,
        RepositorySubfolder = CopyProject ? RepositorySubfolder.Text.Trim() : null,
        Branch = InitialBranch.Text.Trim(),
        StartingBranch = CopyProject && !string.IsNullOrWhiteSpace(StartingBranch.Text) ? StartingBranch.Text.Trim() : null,
        FeatureBranch = CopyProject && !string.IsNullOrWhiteSpace(FeatureBranch.Text) ? FeatureBranch.Text.Trim() : null
    };

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose where to create the new clone folder", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(DestinationFolder.Text.Trim()));
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(SourceFolder) + "-repository";
        DestinationFolder.Text = Path.Combine(picker.FolderName, name);
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!CanContinue) return;
        if (Result != null) { DialogResult = true; return; }
        _busy = true;
        _operation = new();
        Changed();
        try
        {
            if (_review == null)
            {
                SetStatus("Checking the folder and remote repository… Git may ask you to sign in.");
                _review = await GitRepositoryService.PrepareProjectConnectAsync(CaptureRequest(), _operation.Token);
                SetStatus("Review the destination and branch, then connect. Files remain uncommitted until you choose Commit all & push.");
                ContentScroll.ScrollToTop();
                return;
            }
            if (_review.Request.Mode == GitProjectConnectMode.CopyIntoRepository && (_prepare == null || _apply == null))
                throw new InvalidOperationException("Open Git from the project's Launcher dashboard to update its configured folders after copying.");
            if (_prepare != null) _mutationLease = await _prepare(_review.Request);
            if (_connected == null)
            {
                RepositoryOperationAttempted = true;
                var progress = new Progress<string>(message => { if (_busy) SetStatus(message); });
                _connected = await GitRepositoryService.ConnectProjectAsync(_review, progress, _operation.Token);
            }
            SetStatus("Git connection prepared. Updating Launcher…");
            // A failed settings save never causes a second clone or copy. The confirmed result is retained.
            if (_apply != null) await _apply(_connected);
            Result = _connected;
            SetStatus(Result.Message);
        }
        catch (GitCommandExitUnconfirmedException exception)
        {
            _unconfirmed = exception;
            SetStatus(exception.Message + "\nGit process exit is not confirmed. Wait, then choose Check Git exit before closing or retrying.");
        }
        catch (Exception exception)
        {
            SetStatus((_connected != null ? $"Git connection is prepared at {_connected.RepositoryRoot}, but the Launcher update was not confirmed. Retry Launcher update without cloning again.\n\n" : "")
                + exception.Message);
            if (_connected == null) _review = null;
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _busy = false;
            // Keep the dashboard mutation gate while an owned Git command may still be running.
            if (_unconfirmed == null) { _mutationLease?.Dispose(); _mutationLease = null; }
            Changed();
        }
    }

    private async void CheckExit_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCheckExit || _unconfirmed is not { } command) return;
        _busy = true;
        Changed();
        try
        {
            if (!await Task.Run(command.IsExitConfirmed))
            { SetStatus("Git process exit is still unconfirmed. Wait, then check again."); return; }
            _unconfirmed = null;
            if (_connected == null) _review = null;
            _mutationLease?.Dispose();
            _mutationLease = null;
            SetStatus("Git has exited. Inspect the destination for partial files before reviewing another connection. The previous operation was not confirmed.");
        }
        finally { _busy = false; Changed(); }
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (CanEdit) ResetReview(); }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    { _operation?.Cancel(); SetStatus("Cancel requested. Waiting for Git and file operations to stop…"); }
    private void Close_Click(object sender, RoutedEventArgs e) { if (CanClose) Close(); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (CanClose) return;
        e.Cancel = true;
        SetStatus(_unconfirmed != null ? "Confirm Git process exit before closing."
            : "Wait for this operation to finish, or choose Cancel operation.");
    }
}
