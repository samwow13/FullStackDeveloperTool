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

public enum GitReleaseBranchMode { Choose, VerifyLocalMerge, ChooseMissing }

public partial class GitReleaseBranchWindow : Window, IDashboardReplacementWindow
{
    public bool IsDashboardReplacementReady => !_loading && UnconfirmedCommand is null;
    public bool IsDashboardReplacementClosePending => _closing && _loading;

    private readonly Func<CancellationToken, Task<IReadOnlyList<GitRemoteBranchChoice>>> _loadBranches;
    private IReadOnlyList<GitRemoteBranchChoice> _branches = [];
    private readonly string _remote;
    private readonly string _currentBranch;
    private readonly bool _suggestReleaseBranch;
    private readonly string? _cachedDefaultBranch;
    private readonly bool _verifyLocalMerge;
    private readonly string _root;
    private readonly Func<string, CancellationToken, IProgress<string>, Task<GitLocalMergeReview>>? _compare;
    private readonly Func<GitLocalMergeReview, CancellationToken, IProgress<string>, Task<GitLocalMergeOutcome>>? _merge;
    private bool _showingCachedSource;
    private bool _userSelectedBranch;
    private bool _applyingBranchList;
    private string? _selectionToRestore;
    private CancellationTokenSource? _loadCancellation;
    private bool _initialLoadStarted;
    private bool _loading;
    private bool _loaded;
    private bool _loadFailed;
    private bool _closing;
    private bool _motionPreferenceAttached;

    public GitReleaseBranchWindow(string root, string remote,
        Func<CancellationToken, Task<IReadOnlyList<GitRemoteBranchChoice>>> loadBranches,
        string? selected, string currentBranch, GitReleaseBranchMode mode = GitReleaseBranchMode.Choose,
        bool suggestReleaseBranch = false, string? cachedDefaultBranch = null,
        Func<string, CancellationToken, IProgress<string>, Task<GitLocalMergeReview>>? compare = null,
        Func<GitLocalMergeReview, CancellationToken, IProgress<string>, Task<GitLocalMergeOutcome>>? merge = null)
    {
        FullStackLauncher.Services.WindowTheme.Attach(this);
        _loadBranches = loadBranches;
        _remote = remote;
        _currentBranch = currentBranch;
        _suggestReleaseBranch = suggestReleaseBranch;
        _cachedDefaultBranch = cachedDefaultBranch;
        _selectionToRestore = selected;
        _verifyLocalMerge = mode == GitReleaseBranchMode.VerifyLocalMerge;
        _root = root;
        _compare = compare;
        _merge = merge;
        _showingCachedSource = _verifyLocalMerge && !string.IsNullOrWhiteSpace(selected);
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Context.Text = currentBranch;
        if (mode == GitReleaseBranchMode.ChooseMissing)
        {
            Headline.Text = "No release branch detected";
            Description.Text = "Choose the release source for this remote, or cancel and choose it later in Config. Saving this choice does not merge or push.";
        }
        else if (_verifyLocalMerge)
        {
            Title = "Merge locally";
            if (_compare != null) Height = Math.Min(760, SystemParameters.WorkArea.Height);
            Headline.Text = "Is this the correct source branch?";
            Submit.Content = "Refresh & compare";
            Description.Text = _compare == null
                ? "Refresh the source, then automatically preview incoming commits and conflicts. Review the result before approving a local merge." : "";
        }
        if (_showingCachedSource)
        {
            // Existing local evidence renders immediately. No Git command runs until refresh is requested.
            _branches = [new GitRemoteBranchChoice(selected!, "")];
            _loaded = true;
            LoadingStatus.Text = _compare == null ? "Source shown from local configuration or cached refs. Refresh before comparing." : "";
            Height = Math.Min(_compare == null ? 330 : 760, SystemParameters.WorkArea.Height);
            MinHeight = Math.Min(300, SystemParameters.WorkArea.Height);
            ApplySearch();
        }
        UpdateAvailability();
    }

    public GitCommandExitUnconfirmedException? UnconfirmedCommand { get; private set; }
    public string? SelectedBranch => (Branches.SelectedItem as GitRemoteBranchChoice)?.Name;

    private async void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_initialLoadStarted || _closing) return;
        _initialLoadStarted = true;
        SystemParameters.StaticPropertyChanged += MotionPreference_Changed;
        _motionPreferenceAttached = true;
        if (_showingCachedSource) UpdateAvailability();
        else await LoadBranchesAsync();
    }

    private async Task LoadBranchesAsync(bool compareAfterRefresh = false)
    {
        if (_loading || _closing || UnconfirmedCommand != null) return;
        var sourceToCompare = compareAfterRefresh ? SelectedBranch : null;
        _selectionToRestore = SelectedBranch ?? _selectionToRestore;
        _showingCachedSource = false;
        Height = Math.Min(_compare == null ? 490 : 760, SystemParameters.WorkArea.Height);
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
            if (compareAfterRefresh)
            {
                // Never substitute a different source after the user requested this exact branch.
                _selectionToRestore = sourceToCompare;
            }
            else if (_suggestReleaseBranch && !_userSelectedBranch)
            {
                // Reevaluate the entire live list so new or removed release names
                // cannot preserve an ambiguous cached suggestion.
                _selectionToRestore = GitReleaseBranchDetection.Detect(branches.Select(branch => branch.Name), _cachedDefaultBranch).Branch;
            }
            LoadingStatus.Text = branches.Count == 0
                ? "The remote has no branches yet."
                : $"Loaded {branches.Count:N0} remote branch(es). Confirm the source to continue.";
            ApplySearch();
            if (compareAfterRefresh && SelectedBranch != sourceToCompare)
                LoadingStatus.Text = "The source is no longer on this remote. Choose a current branch, then compare.";
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
        if (compareAfterRefresh && !_closing && _loaded && !_loadFailed
            && UnconfirmedCommand == null && sourceToCompare != null && SelectedBranch == sourceToCompare)
        {
            if (_compare != null) await RunComparisonAsync(sourceToCompare);
            else DialogResult = true;
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (TryNavigateMergeFlowBack()) return;
        await LoadBranchesAsync();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (Branches == null || !_loaded || _loading || _closing) return;
        ApplySearch();
    }

    private void ApplySearch()
    {
        var selected = SelectedBranch ?? _selectionToRestore;
        var filtered = _branches.Where(branch => branch.Name.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        _applyingBranchList = true;
        try
        {
            Branches.ItemsSource = filtered;
            Branches.SelectedItem = filtered.FirstOrDefault(branch => branch.Name == selected);
        }
        finally { _applyingBranchList = false; }
        EmptyNotice.Text = _branches.Count == 0
            ? "Publish an initial remote branch before choosing a source, then reload the branches here."
            : "No branches match. Change the search, or cancel and check the remote.";
        EmptyNotice.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    private void Branch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedBranch is { } source)
        {
            _selectionToRestore = source;
            if (!_applyingBranchList && !_loading)
            {
                _userSelectedBranch = true;
            }
        }
        UpdateSelection();
    }

    private void UpdateAvailability()
    {
        BranchPicker.IsEnabled = _loaded && !_loading && !_closing && UnconfirmedCommand == null;
        BranchPicker.Visibility = _showingCachedSource ? Visibility.Collapsed : Visibility.Visible;
        Retry.Visibility = !_loading && !_closing && UnconfirmedCommand == null
            && (_showingCachedSource || _loadFailed || (_loaded && _branches.Count == 0)) ? Visibility.Visible : Visibility.Collapsed;
        Retry.Content = _showingCachedSource ? "Change source…" : _loaded ? "Reload branches" : "Retry";
        Submit.Content = _showingCachedSource ? "Refresh & compare" : _verifyLocalMerge ? "Confirm branch & compare" : "Use this release branch";
        LoadingIndicator.Visibility = _loading || (!_initialLoadStarted && !_showingCachedSource) ? Visibility.Visible : Visibility.Collapsed;
        OperationProgress.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        StatusRow.Visibility = _loading || !string.IsNullOrWhiteSpace(LoadingStatus.Text) ? Visibility.Visible : Visibility.Collapsed;
        Description.Visibility = string.IsNullOrWhiteSpace(Description.Text) ? Visibility.Collapsed : Visibility.Visible;
        UpdateSelection();
        UpdateMergeFlowAvailability();
        UpdateLoadingAnimation();
    }

    private void UpdateSelection()
    {
        if (Submit != null) Submit.IsEnabled = _loaded && !_loading && !_closing
            && UnconfirmedCommand == null && SelectedBranch != null;
        UpdateBranchContext();
        UpdateMergeFlowAvailability();
    }

    private void UpdateBranchContext()
    {
        if (Context == null || Branches == null) return;
        var comparison = _mergeReview?.Comparison;
        var source = comparison?.SourceBranch ?? SelectedBranch ?? _selectionToRestore;
        var local = comparison?.CurrentBranch ?? _currentBranch;
        Context.Text = source == null ? local : $"{local}  ←  {comparison?.Remote ?? _remote}/{source}";
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (await TryAdvanceMergeFlowAsync()) return;
        if (!Submit.IsEnabled || SelectedBranch == null) return;
        if (_showingCachedSource) await LoadBranchesAsync(compareAfterRefresh: true);
        else if (_compare != null) await RunComparisonAsync(SelectedBranch);
        else DialogResult = true;
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
        if (_loading && _mergeFlowStage == MergeFlowStage.Merging)
        {
            // Cancellation can follow a local checkpoint or merge side effect. Keep its outcome visible.
            e.Cancel = true;
            LoadingStatus.Text = "Canceling Git work. Waiting to inspect the local merge result…";
            CancelButton.Content = "Canceling…";
            CancelButton.IsEnabled = false;
            _loadCancellation?.Cancel();
            return;
        }
        _closing = true;
        if (!_loading) return;
        e.Cancel = true;
        LoadingStatus.Text = "Canceling Git work. Waiting for Git to finish before closing…";
        CancelButton.Content = "Canceling…";
        CancelButton.IsEnabled = false;
        UpdateAvailability();
        _loadCancellation?.Cancel();
    }

    private void UpdateLoadingAnimation()
    {
        StopLoadingAnimation();
        if (_showingCachedSource || (!_loading && _initialLoadStarted) || !IsVisible || !SystemParameters.ClientAreaAnimation) return;
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
