using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class AzureDevOpsImportWindow
{
    private readonly ObservableCollection<AzureDevOpsRepositoryRow> _repositoryRows = [];
    private AzureDevOpsRepositoryRow? _inlineRow;
    private IReadOnlyList<AzureDevOpsImportRepository>? _inlineRepositories;
    private IReadOnlyList<string>? _inlineBranches;
    private string? _inlineBranchRepositoryId;
    private Button? _inlineEditButton;
    private bool _updatingInline;

    private bool CanNavigate => CanStartAction && _inlineRow is null;

    private void SynchronizeRepositoryRows()
    {
        for (var index = 0; index < _selectedRepositories.Count; index++)
        {
            var selection = _selectedRepositories[index];
            var row = _repositoryRows.FirstOrDefault(item => ReferenceEquals(item.Selection, selection));
            if (row is null) _repositoryRows.Insert(index, new AzureDevOpsRepositoryRow(selection));
            else if (_repositoryRows.IndexOf(row) != index) _repositoryRows.Move(_repositoryRows.IndexOf(row), index);
        }
        while (_repositoryRows.Count > _selectedRepositories.Count) _repositoryRows.RemoveAt(_repositoryRows.Count - 1);
    }

    private async void EditField_Click(object sender, RoutedEventArgs e)
    {
        if (!CanNavigate || _page != ImportPage.Workspace
            || sender is not Button { DataContext: AzureDevOpsRepositoryRow row, Tag: string fieldName } button
            || !Enum.TryParse<AzureDevOpsRepositoryEditField>(fieldName, out var field)
            || field == AzureDevOpsRepositoryEditField.None) return;
        InvalidateReview();
        ClearError();
        _inlineRow = row;
        _inlineEditButton = button;
        _inlineRepositories = null;
        _inlineBranches = null;
        _inlineBranchRepositoryId = null;
        _updatingInline = true;
        try { row.ResetEditor(field); }
        finally { _updatingInline = false; }
        UpdateControls();
        if (field == AzureDevOpsRepositoryEditField.FeatureBranch) FocusInlineEditor(row, "InlineFeatureBox");

        if (field == AzureDevOpsRepositoryEditField.Repository)
        {
            var version = _selectionVersion;
            await RunLookupAsync(token => AzureDevOpsImportService.ListRepositoriesAsync(row.Selection.ProjectUrl, interactive: false, token),
                repositories =>
                {
                    if (!ReferenceEquals(_inlineRow, row)) return;
                    _inlineRepositories = repositories.Where(repository => !_selectedRepositories.Any(item =>
                        !ReferenceEquals(item, row.Selection) && SameRepository(item.Repository, repository))).ToArray();
                    _updatingInline = true;
                    try
                    {
                        var query = row.DraftRepositorySearch;
                        row.SelectedRepository = _inlineRepositories.FirstOrDefault(repository => SameRepository(repository, row.Selection.Repository));
                        // Keep the prefilled value while fresh repository results load.
                        row.DraftRepositorySearch = query;
                        FilterInlineRepositories(row);
                        RestoreInlineSearch(row, "InlineRepositorySearch", query);
                    }
                    finally { _updatingInline = false; }
                    SetStatus(_inlineRepositories.Count == 0 ? "No other accessible repositories are available." : "");
                }, version, "Loading repositories…");
            if (ReferenceEquals(_inlineRow, row) && CanStartAction && row.SelectedRepository is { } repository)
                await LoadInlineBranchesAsync(row, repository);
        }
        else await LoadInlineBranchesAsync(row, row.Selection.Repository);

        if (ReferenceEquals(_inlineRow, row) && CanStartAction)
            FocusInlineEditor(row, field == AzureDevOpsRepositoryEditField.Repository ? "InlineRepositorySearch"
                : field == AzureDevOpsRepositoryEditField.StartingBranch ? "InlineBranchSearch" : "InlineFeatureBox");
    }

    private async Task LoadInlineBranchesAsync(AzureDevOpsRepositoryRow row, AzureDevOpsImportRepository repository)
    {
        if (!ReferenceEquals(_inlineRow, row) || !CanStartAction) return;
        InvalidateReview();
        ClearError();
        _inlineBranches = null;
        _inlineBranchRepositoryId = null;
        var query = row.DraftStartingBranchSearch;
        _updatingInline = true;
        try
        {
            row.StartingBranchChoices.Clear();
            row.SelectedStartingBranch = null;
            if (row.IsStartingBranchEditing) RestoreInlineSearch(row, "InlineBranchSearch", query);
        }
        finally { _updatingInline = false; }
        var version = _selectionVersion;
        await RunLookupAsync(token => AzureDevOpsImportService.ListBranchesAsync(row.Selection.ProjectUrl, repository.Id, interactive: false, token),
            branches =>
            {
                if (!ReferenceEquals(_inlineRow, row) || row.SelectedRepository?.Id != repository.Id) return;
                _inlineBranches = branches;
                _inlineBranchRepositoryId = repository.Id;
                _updatingInline = true;
                try
                {
                    var branch = branches.Contains(row.Selection.StartingBranch, StringComparer.Ordinal) ? row.Selection.StartingBranch
                        : row.Field == AzureDevOpsRepositoryEditField.Repository && branches.Contains(ShortBranch(repository.DefaultBranch), StringComparer.Ordinal)
                            ? ShortBranch(repository.DefaultBranch) : null;
                    row.SelectedStartingBranch = branch;
                    // Preserve search text typed before or during the metadata lookup.
                    if (row.IsStartingBranchEditing)
                    {
                        row.DraftStartingBranchSearch = query;
                        FilterInlineBranches(row);
                        RestoreInlineSearch(row, "InlineBranchSearch", query);
                    }
                }
                finally { _updatingInline = false; }
                SetStatus(branches.Count == 0 ? "This repository has no branches to clone."
                    : row.Field == AzureDevOpsRepositoryEditField.Repository && row.SelectedStartingBranch is null
                        ? "This repository has no available default branch. Choose another repository."
                        : row.IsStartingBranchEditing && row.SelectedStartingBranch is null ? "Select a starting branch." : "");
            }, version, "Loading branches…");
    }

    private void InlineRepositorySearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updatingInline || sender is not ComboBox { DataContext: AzureDevOpsRepositoryRow row } combo
            || e.OriginalSource is not TextBox box || !ReferenceEquals(_inlineRow, row) || !row.CanSearchRepositories) return;
        var query = box.Text;
        var caret = box.CaretIndex;
        _updatingInline = true;
        try
        {
            row.DraftRepositorySearch = query;
            FilterInlineRepositories(row);
            // Refiltering must not replace the text the developer is typing.
            RestoreInlineSearch(row, "InlineRepositorySearch", query);
            box.CaretIndex = Math.Min(caret, box.Text.Length);
            if (row.SelectedRepository is null)
            {
                _inlineBranches = null;
                _inlineBranchRepositoryId = null;
                row.SelectedStartingBranch = null;
                row.StartingBranchChoices.Clear();
            }
        }
        finally { _updatingInline = false; }
        ClearError();
        SetStatus(row.RepositoryChoices.Count == 0 ? "No matching repositories." : "");
        UpdateControls();
        combo.IsDropDownOpen = combo.IsKeyboardFocusWithin && row.RepositoryChoices.Count > 0;
    }

    private void RestoreInlineSearch(AzureDevOpsRepositoryRow row, string name, string query)
    {
        var combo = RowElement<ComboBox>(row, name);
        combo?.SetCurrentValue(ComboBox.SelectedItemProperty, name == "InlineRepositorySearch"
            ? row.SelectedRepository : (object?)row.SelectedStartingBranch);
        if (name == "InlineRepositorySearch") row.DraftRepositorySearch = query;
        else row.DraftStartingBranchSearch = query;
        combo?.SetCurrentValue(ComboBox.TextProperty, query);
    }

    private void FilterInlineRepositories(AzureDevOpsRepositoryRow row)
    {
        var selected = row.SelectedRepository;
        var query = row.DraftRepositorySearch.Trim();
        row.RepositoryChoices.Clear();
        foreach (var repository in _inlineRepositories ?? [])
            if (repository.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) row.RepositoryChoices.Add(repository);
        row.SelectedRepository = row.RepositoryChoices.FirstOrDefault(repository => repository.Id == selected?.Id);
    }

    private async void InlineRepository_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInline || sender is not ComboBox { DataContext: AzureDevOpsRepositoryRow row } combo
            || !ReferenceEquals(_inlineRow, row) || !row.CanSearchRepositories
            || combo.SelectedItem is not AzureDevOpsImportRepository repository) return;
        _updatingInline = true;
        try
        {
            row.SelectedRepository = repository;
            row.DraftRepositorySearch = repository.Name;
            combo.IsDropDownOpen = false;
        }
        finally { _updatingInline = false; }
        await LoadInlineBranchesAsync(row, repository);
        if (ReferenceEquals(_inlineRow, row) && CanStartAction) FocusInlineEditor(row, "InlineRepositorySearch");
    }

    private void InlineBranchSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updatingInline || sender is not ComboBox { DataContext: AzureDevOpsRepositoryRow row } combo
            || e.OriginalSource is not TextBox box || !ReferenceEquals(_inlineRow, row) || !row.CanSearchBranches) return;
        var query = box.Text;
        var caret = box.CaretIndex;
        _updatingInline = true;
        try
        {
            row.DraftStartingBranchSearch = query;
            FilterInlineBranches(row);
            RestoreInlineSearch(row, "InlineBranchSearch", query);
            box.CaretIndex = Math.Min(caret, box.Text.Length);
        }
        finally { _updatingInline = false; }
        ClearError();
        SetStatus(row.StartingBranchChoices.Count == 0 ? "No matching branches." : "");
        combo.IsDropDownOpen = combo.IsKeyboardFocusWithin && row.StartingBranchChoices.Count > 0;
    }

    private void FilterInlineBranches(AzureDevOpsRepositoryRow row)
    {
        var selected = row.SelectedStartingBranch;
        var query = row.DraftStartingBranchSearch.Trim();
        row.StartingBranchChoices.Clear();
        foreach (var branch in _inlineBranches ?? [])
            if (branch.Contains(query, StringComparison.OrdinalIgnoreCase)) row.StartingBranchChoices.Add(branch);
        row.SelectedStartingBranch = row.StartingBranchChoices.FirstOrDefault(branch => branch == selected);
    }

    private void InlineBranch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInline || sender is not ComboBox { DataContext: AzureDevOpsRepositoryRow row } combo
            || !ReferenceEquals(_inlineRow, row) || combo.SelectedItem is not string branch) return;
        _updatingInline = true;
        try
        {
            row.SelectedStartingBranch = branch;
            row.DraftStartingBranchSearch = branch;
            combo.IsDropDownOpen = false;
        }
        finally { _updatingInline = false; }
        ClearError();
        SetStatus("");
    }

    private void SaveInlineEdit_Click(object sender, RoutedEventArgs e) => SaveInlineEdit();

    private void SaveInlineEdit()
    {
        if (!CanStartAction || _inlineRow is not { CanSave: true } row || _inlineBranches is null
            || row.SelectedRepository is not { } repository || _inlineBranchRepositoryId != repository.Id) return;
        var previous = row.Selection;
        var startingBranch = row.Field == AzureDevOpsRepositoryEditField.FeatureBranch ? previous.StartingBranch : row.SelectedStartingBranch;
        if (startingBranch is null || !_inlineBranches.Contains(startingBranch, StringComparer.Ordinal))
        {
            ReportError("Select an available starting branch.", new InvalidOperationException("The starting branch must exist in the selected repository."));
            return;
        }
        if (row.Field == AzureDevOpsRepositoryEditField.Repository && (_inlineRepositories is null
            || !_inlineRepositories.Contains(repository))) return;
        var featureBranch = row.IsFeatureBranchEditing ? row.DraftFeatureBranch.Trim() : previous.FeatureBranch;
        if (!IsFeatureBranchName(featureBranch))
        {
            ReportError("Enter a valid feature branch.", new InvalidOperationException("Use a Git branch name such as feature/my-change."));
            FocusInlineEditor(row, "InlineFeatureBox");
            return;
        }
        if (_inlineBranches.Any(branch => BranchNamesConflict(branch, featureBranch)))
        {
            ReportError("Choose a new feature branch.", new InvalidOperationException(row.Field == AzureDevOpsRepositoryEditField.FeatureBranch
                ? "The feature branch conflicts with an existing repository branch."
                : "Cancel this edit and change the feature branch before choosing this repository or starting branch."));
            return;
        }
        if (_selectedRepositories.Any(item => !ReferenceEquals(item, previous) && SameRepository(item.Repository, repository)))
        {
            ReportError("This repository is already added.", new InvalidOperationException("Choose another repository."));
            return;
        }
        var index = _selectedRepositories.IndexOf(previous);
        if (index < 0) return;
        var selection = previous with { Repository = repository, StartingBranch = startingBranch, FeatureBranch = featureBranch };
        selection = selection with { LocalFolderName = ChooseRepositoryFolderName(selection, previous) };
        InvalidateReview();
        ClearError();
        row.UpdateSelection(selection);
        _selectedRepositories[index] = selection;
        FinishInlineEdit();
    }

    private void CancelInlineEdit_Click(object sender, RoutedEventArgs e) => CancelInlineEdit();

    private void CancelInlineEdit()
    {
        if (_inlineRow is null) return;
        InvalidateReview();
        if (_unconfirmedCommands.Count == 0) ClearError();
        FinishInlineEdit();
    }

    private void FinishInlineEdit()
    {
        var row = _inlineRow;
        CloseInlineDropdown();
        _inlineRow = null;
        _inlineRepositories = null;
        _inlineBranches = null;
        _inlineBranchRepositoryId = null;
        _updatingInline = true;
        try { row?.FinishEditor(); }
        finally { _updatingInline = false; }
        if (_unconfirmedCommands.Count == 0) SetStatus("");
        UpdateControls();
        var button = _inlineEditButton;
        _inlineEditButton = null;
        if (CanStartAction && button is not null) button.Focus();
    }

    private void UpdateInlineControls(bool ready)
    {
        foreach (var row in _repositoryRows)
        {
            var active = ReferenceEquals(_inlineRow, row);
            row.CanEdit = ready && _inlineRow is null;
            row.CanCancel = active && !_closeRequested && !_checkingExit;
            row.CanSearchRepositories = active && ready && _inlineRepositories is not null;
            row.CanSearchBranches = active && ready && _inlineBranches is not null;
            row.CanEditFeatureBranch = active && !_closeRequested && !_checkingExit && _unconfirmedCommands.Count == 0;
            row.IsEditorReady = active && ready && _inlineBranches is not null
                && _inlineBranchRepositoryId == row.SelectedRepository?.Id;
        }
    }

    private void InlineEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_inlineRow is not { } row || e.Key != Key.Enter || Keyboard.FocusedElement is Button) return;
        // Let the dropdown accept its highlighted result before the field is saved.
        if (RowElement<ComboBox>(row, "InlineRepositorySearch") is { IsDropDownOpen: true }
            || RowElement<ComboBox>(row, "InlineBranchSearch") is { IsDropDownOpen: true }) return;
        SaveInlineEdit();
        e.Handled = true;
    }

    private bool CloseInlineDropdown()
    {
        if (_inlineRow is not { } row) return false;
        foreach (var name in new[] { "InlineRepositorySearch", "InlineBranchSearch" })
        {
            if (RowElement<ComboBox>(row, name) is not { IsDropDownOpen: true } combo) continue;
            combo.IsDropDownOpen = false;
            return true;
        }
        return false;
    }

    private void FocusInlineEditor(AzureDevOpsRepositoryRow row, string name) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!ReferenceEquals(_inlineRow, row) || _closed || _closeRequested) return;
            var box = RowElement<TextBox>(row, name);
            if (box is null && RowElement<ComboBox>(row, name) is { IsEnabled: true } combo)
            {
                combo.ApplyTemplate();
                box = combo.Template.FindName("PART_EditableTextBox", combo) as TextBox;
            }
            if (box is not { IsEnabled: true } || box.IsKeyboardFocusWithin) return;
            box.Focus();
            box.SelectAll();
        }));

    private T? RowElement<T>(AzureDevOpsRepositoryRow row, string name) where T : FrameworkElement
    {
        if (SelectedRepositoriesList.ItemContainerGenerator.ContainerFromItem(row) is not DependencyObject container) return null;
        return FindRowElement<T>(container, name);
    }

    private static T? FindRowElement<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        if (parent is T element && element.Name == name) return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindRowElement<T>(VisualTreeHelper.GetChild(parent, index), name) is { } child) return child;
        return null;
    }
}
