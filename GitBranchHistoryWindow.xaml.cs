using System.ComponentModel;
using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class GitBranchHistoryWindow : Window, INotifyPropertyChanged
{
    private readonly GitBranchComparison _comparison;
    private GitBranchHistoryPage? _page;
    private CancellationTokenSource? _loadCancellation;
    private bool _closing;
    private bool _loading;
    private bool _loadFailed;
    private string _historyStatus = "";

    public GitBranchHistoryWindow(GitBranchComparison comparison)
    {
        _comparison = comparison;
        InitializeComponent();
        MembershipColumn.Visibility = comparison.SelectedBranchOnly ? Visibility.Collapsed : Visibility.Visible;
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        DataContext = this;
    }

    public string BranchTitle => _comparison.SelectedBranchName;
    public string ComparisonSummary => _comparison.SelectedBranchOnly
        ? $"{(_comparison.SelectedIsRemote ? "Remote" : "Selected branch")} {_comparison.SelectedBranchName}: {_comparison.TotalCommits:N0} commits · {(_comparison.SelectedIsRemote ? "last fetched" : "local history")}"
        : $"Local {_comparison.CurrentBranch}: {_comparison.LocalOnlyCommits:N0} ahead · {_comparison.SelectedOnlyCommits:N0} behind · {(_comparison.SelectedIsRemote ? "last fetched" : "local history")}";
    public string HistoryDescription => _comparison.SelectedBranchOnly
        ? "Shows committed history reachable from the selected branch only. Uncommitted changes are excluded; remote history is last fetched."
        : "Ahead means commits found only on your current local branch. Behind means commits found only on the selected branch. Uncommitted changes are excluded; remote history is last fetched.";
    public string HistoryAccessibleName => _comparison.SelectedBranchOnly
        ? "Paginated selected branch commit history"
        : "Paginated branch commit history and commit membership";
    public IReadOnlyList<GitBranchHistoryCommit> Commits => _page?.Commits ?? [];
    public bool IsLoading => _loading;
    public bool CanLoadPrevious => !_loading && !_loadFailed && _page?.HasPreviousPage == true;
    public bool CanLoadNext => !_loading && !_loadFailed && _page?.HasNextPage == true;
    public string HistoryStatus => _historyStatus;
    public string PageLabel => _page == null ? "" : $"Page {_page.PageIndex + 1:N0} of {Math.Max(1, (_page.TotalCommits + _page.PageSize - 1) / _page.PageSize):N0} · {_page.TotalCommits:N0} commits";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<GitCommandExitUnconfirmedException>? CommandExitUnconfirmed;
    private void Changed() => PropertyChanged?.Invoke(this, new(null));

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadPageAsync(0);
    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (CanLoadPrevious && _page != null) await LoadPageAsync(_page.PageIndex - 1);
    }
    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (CanLoadNext && _page != null) await LoadPageAsync(_page.PageIndex + 1);
    }

    private async Task LoadPageAsync(int pageIndex)
    {
        if (_loading || _closing) return;
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(60));
        _loadCancellation = cancellation;
        _loading = true;
        _loadFailed = false;
        _historyStatus = "Loading commits…";
        Changed();
        try
        {
            var page = await Task.Run(() => GitRepositoryService.ReadBranchHistoryPageAsync(_comparison, pageIndex, cancellation.Token));
            if (_closing) return;
            cancellation.Token.ThrowIfCancellationRequested();
            _page = page;
            _historyStatus = "";
        }
        catch (Exception exception)
        {
            if (exception is GitCommandExitUnconfirmedException unconfirmed)
                CommandExitUnconfirmed?.Invoke(unconfirmed);
            if (_closing) return;
            _loadFailed = true;
            _historyStatus = SensitiveDataProtection.Redact(exception.Message)
                + "\nClose history, refresh local status, and select the branch again.";
        }
        finally
        {
            _loadCancellation = null;
            _loading = false;
            if (_closing) Close();
            else Changed();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _loadCancellation?.Cancel();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        if (_loading)
        {
            e.Cancel = true;
            _historyStatus = "Canceling commit history load before closing…";
            Changed();
        }
        _loadCancellation?.Cancel();
    }
}
