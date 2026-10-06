using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FullStackLauncher.Models;

public enum AzureDevOpsRepositoryEditField
{
    None,
    Repository,
    StartingBranch,
    FeatureBranch
}

public sealed class AzureDevOpsRepositoryRow : INotifyPropertyChanged
{
    private AzureDevOpsWorkspaceRepository _selection;
    private AzureDevOpsRepositoryEditField _field;
    private string _draftFeatureBranch = "";
    private string _draftRepositorySearch = "";
    private string _draftStartingBranchSearch = "";
    private AzureDevOpsImportRepository? _selectedRepository;
    private string? _selectedStartingBranch;
    private bool _isEditorReady;
    private bool _canEdit = true;
    private bool _canCancel = true;
    private bool _canSearchRepositories;
    private bool _canSearchBranches;
    private bool _canEditFeatureBranch;

    public AzureDevOpsRepositoryRow(AzureDevOpsWorkspaceRepository selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        _selection = selection;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AzureDevOpsWorkspaceRepository Selection
    {
        get => _selection;
        private set
        {
            if (_selection == value)
                return;
            _selection = value;
            Changed();
            Changed(nameof(StartingBranchDisplay));
        }
    }

    public AzureDevOpsRepositoryEditField Field
    {
        get => _field;
        set
        {
            if (_field == value)
                return;
            _field = value;
            Changed();
            Changed(nameof(IsEditing));
            Changed(nameof(IsRepositoryEditing));
            Changed(nameof(IsStartingBranchEditing));
            Changed(nameof(IsFeatureBranchEditing));
            Changed(nameof(StartingBranchDisplay));
            Changed(nameof(CanSave));
        }
    }

    public bool IsEditing => Field != AzureDevOpsRepositoryEditField.None;
    public bool IsRepositoryEditing => Field == AzureDevOpsRepositoryEditField.Repository;
    public bool IsStartingBranchEditing => Field == AzureDevOpsRepositoryEditField.StartingBranch;
    public bool IsFeatureBranchEditing => Field == AzureDevOpsRepositoryEditField.FeatureBranch;
    public string StartingBranchDisplay => IsRepositoryEditing && SelectedStartingBranch is not null
        ? SelectedStartingBranch
        : Selection.StartingBranch;

    public string DraftFeatureBranch
    {
        get => _draftFeatureBranch;
        set => SetValue(ref _draftFeatureBranch, value);
    }

    public string DraftRepositorySearch
    {
        get => _draftRepositorySearch;
        set => SetValue(ref _draftRepositorySearch, value);
    }

    public string DraftStartingBranchSearch
    {
        get => _draftStartingBranchSearch;
        set => SetValue(ref _draftStartingBranchSearch, value);
    }

    public ObservableCollection<AzureDevOpsImportRepository> RepositoryChoices { get; } = [];
    public ObservableCollection<string> StartingBranchChoices { get; } = [];

    public AzureDevOpsImportRepository? SelectedRepository
    {
        get => _selectedRepository;
        set
        {
            if (!SetValue(ref _selectedRepository, value))
                return;
            Changed(nameof(CanSave));
        }
    }

    public string? SelectedStartingBranch
    {
        get => _selectedStartingBranch;
        set
        {
            if (!SetValue(ref _selectedStartingBranch, value))
                return;
            Changed(nameof(StartingBranchDisplay));
            Changed(nameof(CanSave));
        }
    }

    public bool IsEditorReady
    {
        get => _isEditorReady;
        set
        {
            if (!SetValue(ref _isEditorReady, value))
                return;
            Changed(nameof(CanSave));
        }
    }

    public bool CanEdit
    {
        get => _canEdit;
        set => SetValue(ref _canEdit, value);
    }

    public bool CanCancel
    {
        get => _canCancel;
        set => SetValue(ref _canCancel, value);
    }

    public bool CanSearchRepositories
    {
        get => _canSearchRepositories;
        set => SetValue(ref _canSearchRepositories, value);
    }

    public bool CanSearchBranches
    {
        get => _canSearchBranches;
        set => SetValue(ref _canSearchBranches, value);
    }

    public bool CanEditFeatureBranch
    {
        get => _canEditFeatureBranch;
        set => SetValue(ref _canEditFeatureBranch, value);
    }

    public bool CanSave => IsEditorReady && (Field switch
    {
        AzureDevOpsRepositoryEditField.Repository => SelectedRepository is not null &&
            !string.IsNullOrWhiteSpace(SelectedStartingBranch),
        AzureDevOpsRepositoryEditField.StartingBranch => !string.IsNullOrWhiteSpace(SelectedStartingBranch),
        AzureDevOpsRepositoryEditField.FeatureBranch => true,
        _ => false
    });

    public void UpdateSelection(AzureDevOpsWorkspaceRepository selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Selection = selection;
    }

    public void ResetEditor(AzureDevOpsRepositoryEditField field)
    {
        IsEditorReady = false;
        SelectedRepository = Selection.Repository;
        SelectedStartingBranch = field == AzureDevOpsRepositoryEditField.Repository
            ? null
            : Selection.StartingBranch;
        Field = field;
        DraftFeatureBranch = Selection.FeatureBranch;
        DraftRepositorySearch = Selection.Repository.Name;
        DraftStartingBranchSearch = Selection.StartingBranch;
    }

    public void FinishEditor()
    {
        Field = AzureDevOpsRepositoryEditField.None;
        IsEditorReady = false;
        RepositoryChoices.Clear();
        StartingBranchChoices.Clear();
    }

    private bool SetValue<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Changed(propertyName);
        return true;
    }

    private void Changed([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
