using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public enum GitReleaseBranchMode { Choose, VerifyLocalMerge, VerifyConflictTest }

public partial class GitReleaseBranchWindow : Window
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<GitRemoteBranchChoice>>> _loadBranches;
    private IReadOnlyList<GitRemoteBranchChoice> _branches = [];
    private readonly string _remote;
    private readonly string _currentBranch;
    private string? _selectionToRestore;
    private CancellationTokenSource? _loadCancellation;
    private bool _initialLoadStarted;
    private bool _loading;
    private bool _loaded;
    private bool _loadFailed;
    private bool _closing;
    private bool _motionPreferenceAttached;

    public GitReleaseBranchWindow(string root, string remote, string fetchUrl,
        Func<CancellationToken, Task<IReadOnlyList<GitRemoteBranchChoice>>> loadBranches,
        string? selected, string currentBranch, GitReleaseBranchMode mode = GitReleaseBranchMode.Choose)
    {
        _loadBranches = loadBranches;
        _remote = remote;
        _currentBranch = currentBranch;
        _selectionToRestore = selected;
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Context.Text = $"Current local branch: {currentBranch}\nLoading branches from {remote}…";
        RepositoryContext.Text = SensitiveDataProtection.Redact($"Repository: {root}\nSource remote: {remote}\n{fetchUrl}");
        if (mode != GitReleaseBranchMode.Choose)
        {
            Title = "Verify remote source branch";
            Headline.Text = mode == GitReleaseBranchMode.VerifyLocalMerge
                ? "Verify remote source before merging" : "Verify remote source before testing";
            Submit.Content = "Confirm branch & compare";
            Description.Text = mode == GitReleaseBranchMode.VerifyLocalMerge
                ? "Confirm this remote source to check for incoming commits and merge conflicts now. If your current branch already includes the source, return to Git to commit your work. Otherwise review the comparison, then approve the local merge. Selecting a different source saves it for this remote."
                : "Confirm this remote source to compare committed branches now and show the conflict result here. Selecting a different source saves it for this remote. Uncommitted edits are excluded. The test does not merge, stage, commit, push, or change local files.";
        }
        UpdateSelection();
    }

    public GitCommandExitUnconfirmedException? UnconfirmedCommand { get; private set; }
    public string? SelectedBranch => (Branches.SelectedItem as GitRemoteBranchChoice)?.Name;

    private async void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_initialLoadStarted || _closing) return;
        _initialLoadStarted = true;
        SystemParameters.StaticPropertyChanged += MotionPreference_Changed;
        _motionPreferenceAttached = true;
        await LoadBranchesAsync();
    }

    private async Task LoadBranchesAsync()
    {
        if (_loading || _closing || UnconfirmedCommand != null) return;
        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _loading = true;
        _loaded = false;
        _loadFailed = false;
        _branches = [];
        Branches.ItemsSource = null;
        ErrorDetails.Text = "";
        ErrorDetails.Visibility = Visibility.Collapsed;
        EmptyNotice.Visibility = Visibility.Collapsed;
        LoadingStatus.Text = $"Loading branches from {_remote}…";
        Context.Text = $"Current local branch: {_currentBranch}\nReading the remote branch list now.";
        UpdateAvailability();
        try
        {
            // Let the modal paint before starting its loader. The loader owns any
            // background Git work and can update the owner's WPF state safely.
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            cancellation.Token.ThrowIfCancellationRequested();
            var branches = await _loadBranches(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closing) return;
            _branches = branches;
            _loaded = true;
            Context.Text = $"Current local branch: {_currentBranch}\nBranches read from {_remote} just now.";
            LoadingStatus.Text = branches.Count == 0
                ? "The remote has no branches yet."
                : $"Loaded {branches.Count:N0} remote branch(es). Confirm the source to continue.";
            ApplySearch();
        }
        catch (Exception exception)
        {
            // Preserve process identity even when cancellation requested this close.
            // The owner must hold further Git work until that exit is confirmed.
            if (exception is GitCommandExitUnconfirmedException unconfirmed) UnconfirmedCommand = unconfirmed;
            if (_closing) return;
            _loaded = false;
            _branches = [];
            Branches.ItemsSource = null;
            _loadFailed = true;
            Context.Text = $"Current local branch: {_currentBranch}\nThe remote branch list could not be verified.";
            LoadingStatus.Text = UnconfirmedCommand != null
                ? "Git process exit is not confirmed. Cancel, then use Retry in Git to check its exit before continuing."
                : exception is OperationCanceledException
                    ? "Loading was canceled. Retry to read the remote branches again."
                    : "Could not load remote branches. Retry, or cancel and review the connection.";
            ErrorDetails.Text = SensitiveDataProtection.Redact(exception.Message);
            ErrorDetails.Visibility = Visibility.Visible;
        }
        finally
        {
            _loadCancellation = null;
            _loading = false;
            if (_closing)
            {
                StopLoadingAnimation();
                Close();
            }
            else
            {
                UpdateAvailability();
                if (_loaded && _branches.Count > 0) Search.Focus();
            }
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await LoadBranchesAsync();

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (Branches == null || !_loaded || _loading || _closing) return;
        ApplySearch();
    }

    private void ApplySearch()
    {
        var selected = SelectedBranch ?? _selectionToRestore;
        var filtered = _branches.Where(branch => branch.Name.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        Branches.ItemsSource = filtered;
        Branches.SelectedItem = filtered.FirstOrDefault(branch => branch.Name == selected);
        EmptyNotice.Text = _branches.Count == 0
            ? "Publish an initial remote branch before choosing a source, then reload the branches here."
            : "No branches match. Change the search, or cancel and check the remote.";
        EmptyNotice.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    private void Branch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedBranch is { } source) _selectionToRestore = source;
        UpdateSelection();
    }

    private void UpdateAvailability()
    {
        BranchPicker.IsEnabled = _loaded && !_loading && !_closing && UnconfirmedCommand == null;
        Retry.Visibility = !_loading && !_closing && UnconfirmedCommand == null
            && (_loadFailed || (_loaded && _branches.Count == 0)) ? Visibility.Visible : Visibility.Collapsed;
        Retry.Content = _loaded ? "Reload branches" : "Retry";
        LoadingIndicator.Visibility = _loading || !_initialLoadStarted ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
        UpdateLoadingAnimation();
    }

    private void UpdateSelection()
    {
        if (Submit != null) Submit.IsEnabled = _loaded && !_loading && !_closing
            && UnconfirmedCommand == null && SelectedBranch != null;
        if (SourceSummary != null) SourceSummary.Text = !_initialLoadStarted || _loading
            ? "Loading remote branches before selecting the source…"
            : !_loaded ? "A verified remote branch list is required to continue."
            : SelectedBranch is { } source ? $"Release source: {_remote}/{source}" : "Select a release branch to continue.";
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (Submit.IsEnabled && SelectedBranch != null) DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        if (!_loading) return;
        e.Cancel = true;
        LoadingStatus.Text = "Canceling branch loading. Waiting for Git to finish before closing…";
        CancelButton.Content = "Canceling…";
        CancelButton.IsEnabled = false;
        UpdateAvailability();
        _loadCancellation?.Cancel();
    }

    private void UpdateLoadingAnimation()
    {
        StopLoadingAnimation();
        if ((!_loading && _initialLoadStarted) || !IsVisible || !SystemParameters.ClientAreaAnimation) return;
        LoadingRotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void StopLoadingAnimation() => LoadingRotate.BeginAnimation(RotateTransform.AngleProperty, null);

    private void MotionPreference_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        Dispatcher.BeginInvoke(new Action(UpdateLoadingAnimation));
    }

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (LoadingRotate != null) UpdateLoadingAnimation();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        StopLoadingAnimation();
        if (_motionPreferenceAttached) SystemParameters.StaticPropertyChanged -= MotionPreference_Changed;
        _motionPreferenceAttached = false;
    }
}
