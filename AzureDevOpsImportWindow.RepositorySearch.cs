using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class AzureDevOpsImportWindow
{
    private string _repositorySearch = "";
    private IReadOnlyList<string> _recentRepositoryIds = [];

    private IEnumerable<AzureDevOpsImportRepository> AvailableRepositories() => _repositories.Where(repository =>
        !_selectedRepositories.Any(item => SameRepository(item.Repository, repository)));

    private AzureDevOpsImportRepository[] FilterRepositories(string query) => AvailableRepositories()
        .Where(repository => repository.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();

    private void ClearRepositorySearch()
    {
        _repositorySearch = "";
        _recentRepositoryIds = [];
        _updatingSelections = true;
        try
        {
            RepositoryBox.ItemsSource = null;
            RepositoryBox.Text = "";
            RepositoryBox.IsDropDownOpen = false;
        }
        finally { _updatingSelections = false; }
        PopulateRecentRepositories();
    }

    private void PopulateRecentRepositories()
    {
        var available = AvailableRepositories().ToArray();
        var favorites = _recentRepositoryIds.Select(id => available.FirstOrDefault(repository =>
            string.Equals(repository.Id, id, StringComparison.OrdinalIgnoreCase)))
            .OfType<AzureDevOpsImportRepository>().Take(10).ToArray();
        RecentRepositoriesList.ItemsSource = favorites;
        RecentRepositoryPanel.Visibility = favorites.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RepositorySearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updatingSelections || CloneButton is null || !CanNavigate || e.OriginalSource is not TextBox box) return;
        var query = box.Text;
        // Selecting an item also updates the editable text; that is not a new search.
        if (RepositoryBox.SelectedItem is AzureDevOpsImportRepository selected && query == selected.Name) return;

        var start = box.SelectionStart;
        var length = box.SelectionLength;
        InvalidateReview();
        ClearError();
        _repositorySearch = query;
        _draftRepositoryId = null;
        _draftStartingBranch = null;
        _draftFeatureBranch = DefaultFeatureBranch;
        ResetBranchChoices();
        _updatingSelections = true;
        try
        {
            RepositoryBox.SelectedItem = null;
            RepositoryBox.ItemsSource = FilterRepositories(query);
            RepositoryBox.Text = query;
        }
        finally { _updatingSelections = false; }
        SetStatus(RepositoryChoicesStatus());
        UpdateControls();
        RepositoryBox.IsDropDownOpen = RepositoryBox.IsKeyboardFocusWithin && RepositoryBox.Items.Count > 0;
        // WPF selects all editable text when opening the popup. Restore the user's
        // caret afterward so the next keystroke extends the search instead of replacing it.
        var selectionStart = Math.Min(start, box.Text.Length);
        box.Select(selectionStart, Math.Min(length, box.Text.Length - selectionStart));
    }

    private void RepositoryDropdown_Opened(object sender, EventArgs e)
    {
        if (_updatingSelections || RepositoryBox.SelectedItem is not AzureDevOpsImportRepository selected) return;
        _updatingSelections = true;
        try
        {
            RepositoryBox.ItemsSource = AvailableRepositories().ToArray();
            RepositoryBox.SelectedItem = selected;
            RepositoryBox.Text = selected.Name;
        }
        finally { _updatingSelections = false; }
    }

    private void RecentRepository_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNavigate || _loadedProjectUrl is null || sender is not Button { DataContext: AzureDevOpsImportRepository favorite }) return;
        var repository = AvailableRepositories().FirstOrDefault(item => SameRepository(item, favorite));
        if (repository is null) return;
        InvalidateReview();
        ClearError();
        _draftRepositoryId = repository.Id;
        _draftStartingBranch = null;
        _draftFeatureBranch = DefaultFeatureBranch;
        ResetBranchChoices();
        _repositorySearch = repository.Name;
        PopulateRepositoryChoices();
        RepositoryBox.IsDropDownOpen = false;
        SetStatus("");
        UpdateControls();
        NextButton.Focus();
    }

    private async Task RememberRepositoryAsync(string projectUrl, string repositoryId, int version)
    {
        try
        {
            var recent = await Task.Run(() =>
            {
                AzureDevOpsImportPreferences.RememberRepository(projectUrl, repositoryId);
                return AzureDevOpsImportPreferences.LoadRecentRepositoryIds(projectUrl);
            });
            if (_closed || _closeRequested || version != _selectionVersion || _loadedProjectUrl != projectUrl) return;
            _recentRepositoryIds = recent;
            PopulateRecentRepositories();
        }
        catch (Exception exception)
        {
            if (!_closed && !_closeRequested && version == _selectionVersion && _loadedProjectUrl == projectUrl)
                ReportError("Repository ready, but recent favorites could not be remembered.", exception);
        }
    }
}
