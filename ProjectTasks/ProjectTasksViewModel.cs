using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using FullStackLauncher.Models;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Local queue preparation. Execution is deliberately owned by a future coordinator.</summary>
public sealed class ProjectTasksViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ProjectTaskStore _store = new();
    private readonly CodexModelCatalog _catalog = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, NoteDraft> _drafts = new(StringComparer.Ordinal);
    private ProjectTaskData _data;
    private ProjectProfile? _project;
    private string _folder = "";
    private NoteRow? _selectedNote;
    private QueueRow? _selectedQueue;
    private string _editorName = "";
    private string _editorPrompt = "";
    private string _feedback = "Saving a note keeps it local. Add it to the queue when it is ready.";
    private string _modelStatus = "Refresh models to load the installed Codex model and thinking-level choices.";
    private bool _refreshingRows;
    private bool _loadingModels;
    private bool _disposed;
    private CodexModelOption? _selectedModel;
    private CodexReasoningEffortOption? _selectedEffort;

    public ProjectTasksViewModel()
    {
        _data = _store.Load();
        if (_store.LoadWarning is { } warning) _feedback = warning;
        NewNoteCommand = new TaskPanelCommand(NewNote, () => CanEdit);
        SaveNoteCommand = new TaskPanelCommand(() => SaveCurrentNote(), () => CanEdit && HasCurrentDraft);
        SaveAllDraftsCommand = new TaskPanelCommand(() => SaveAllDrafts(), () => _store.CanSave && HasDrafts);
        DiscardDraftCommand = new TaskPanelCommand(DiscardCurrentDraft, () => HasCurrentDraft);
        DeleteNoteCommand = new TaskPanelCommand(DeleteNote, () => CanEdit && SelectedNote != null);
        ToggleCompleteCommand = new TaskPanelCommand(ToggleComplete, () => CanEdit && SelectedNote is { IsDraftOnly: false });
        MoveNoteUpCommand = new TaskPanelCommand(() => MoveNote(-1), () => CanMoveNote(-1));
        MoveNoteDownCommand = new TaskPanelCommand(() => MoveNote(1), () => CanMoveNote(1));
        AddToQueueCommand = new TaskPanelCommand(AddToQueue, () => CanEdit && SelectedNote is { IsCompleted: false, IsDraftOnly: false } && !SelectedNote.IsQueued && !HasCurrentDraft);
        RemoveFromQueueCommand = new TaskPanelCommand(RemoveFromQueue, () => CanEdit && SelectedQueue != null);
        MoveQueueUpCommand = new TaskPanelCommand(() => MoveQueue(-1), () => CanMoveQueue(-1));
        MoveQueueDownCommand = new TaskPanelCommand(() => MoveQueue(1), () => CanMoveQueue(1));
        ToggleItemCommand = new TaskPanelCommand(ToggleQueueItem, () => CanEdit && SelectedQueue != null);
        RefreshModelsCommand = new TaskPanelCommand(async () => await RefreshModelsAsync(), () => !_loadingModels);
        UseCurrentFolderCommand = new TaskPanelCommand(UseCurrentFolder, () => CanEdit && FolderChanged && !HasOutstandingRun);
        ReloadCommand = new TaskPanelCommand(Reload, () => true);
    }

    public ObservableCollection<NoteRow> Notes { get; } = [];
    public ObservableCollection<QueueRow> Queue { get; } = [];
    public ObservableCollection<CodexModelOption> Models { get; } = [];
    public ObservableCollection<CodexReasoningEffortOption> Efforts { get; } = [];
    public string ProjectName => _project?.Name ?? "Choose a project in the dashboard";
    public string? ProjectId => _project?.Id;
    public string ProjectFolder => _project == null ? "Notes follow the selected launcher project." : _folder;
    public string StorePath => _store.StorePath;
    public bool CanEdit => _project != null && _store.CanSave;
    public bool HasProject => _project != null;
    public bool HasDrafts => _drafts.Count > 0;
    public bool HasCurrentDraft => _project != null && _drafts.ContainsKey(DraftKey(_project.Id, _selectedNote?.Id));
    public bool HasSelectedNote => _selectedNote != null;
    public bool HasSelectedQueue => _selectedQueue != null;
    public bool CanChooseModel => CanEdit && HasSelectedQueue && Models.Count > 0 && !_loadingModels;
    public bool HasOutstandingRun => _data.Receipts.Any(x => x.Purpose == ProjectTaskExecutionPurpose.QueueItem && x.Snapshot.ProjectId == _project?.Id && x.State is ProjectTaskRunState.Prepared or ProjectTaskRunState.Starting or ProjectTaskRunState.Running or ProjectTaskRunState.Recovering or ProjectTaskRunState.NeedsAttention)
        || Queue.Any(x => x.State is ProjectQueueItemState.Starting or ProjectQueueItemState.Running or ProjectQueueItemState.Recovering);
    public bool FolderChanged => _project != null && _data.Queues.FirstOrDefault(x => x.ProjectId == _project.Id) is { } queue && !queue.AssignedFolder.Equals(_folder, StringComparison.OrdinalIgnoreCase);
    public string AssignedFolder => _data.Queues.FirstOrDefault(x => x.ProjectId == _project?.Id)?.AssignedFolder ?? _folder;
    public string QueueStatus => HasOutstandingRun ? "Execution status unavailable — automatic execution is not connected in this build." : "Queue paused · automatic execution is not available in this build";
    public string NoteCount => $"{Notes.Count} notes · {Notes.Count(x => x.IsCompleted)} complete";
    public string QueueCount => $"{Queue.Count} items · {Queue.Count(x => x.Enabled)} enabled";
    public string DraftStatus => HasDrafts ? $"{_drafts.Count} unsaved draft(s) across projects. Save all drafts before leaving." : "All note changes saved locally.";
    public string EditorHeading => SelectedNote == null ? "New note" : "Edit note";
    public string CompleteLabel => SelectedNote?.IsCompleted == true ? "Reopen note" : "Mark complete";
    public string ItemEnabledLabel => SelectedQueue?.Enabled == true ? "Disable item" : "Enable item";
    public string Feedback { get => _feedback; private set { _feedback = value; Changed(); } }
    public string ModelStatus { get => _modelStatus; private set { _modelStatus = value; Changed(); } }
    public string SavedQueueOptions => SelectedQueue == null ? "Select an item to configure it." : $"Saved: {SelectedQueue.Options}";
    public string EditorName { get => _editorName; set { _editorName = value; Changed(); CaptureDraft(); } }
    public string EditorPrompt { get => _editorPrompt; set { _editorPrompt = value; Changed(); CaptureDraft(); } }

    public NoteRow? SelectedNote
    {
        get => _selectedNote;
        set
        {
            if (_refreshingRows || ReferenceEquals(_selectedNote, value)) return;
            _selectedNote = value;
            LoadEditor();
            RefreshBindings();
        }
    }

    public QueueRow? SelectedQueue
    {
        get => _selectedQueue;
        set
        {
            if (_refreshingRows || ReferenceEquals(_selectedQueue, value)) return;
            _selectedQueue = value;
            LoadQueueOptions();
            RefreshBindings();
        }
    }

    public CodexModelOption? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (_refreshingRows || value == null || _selectedQueue == null || _selectedModel?.Id == value.Id) return;
            var effort = value.SupportedReasoningEfforts.FirstOrDefault(x => x.Id == value.DefaultReasoningEffort) ?? value.SupportedReasoningEfforts.FirstOrDefault();
            if (effort == null) return;
            SaveQueueOptions(value.Id, effort.Id);
        }
    }

    public CodexReasoningEffortOption? SelectedEffort
    {
        get => _selectedEffort;
        set
        {
            if (_refreshingRows || value == null || _selectedModel == null || _selectedQueue == null || _selectedEffort?.Id == value.Id) return;
            SaveQueueOptions(_selectedModel.Id, value.Id);
        }
    }

    public ICommand NewNoteCommand { get; }
    public ICommand SaveNoteCommand { get; }
    public ICommand SaveAllDraftsCommand { get; }
    public ICommand DiscardDraftCommand { get; }
    public ICommand DeleteNoteCommand { get; }
    public ICommand ToggleCompleteCommand { get; }
    public ICommand MoveNoteUpCommand { get; }
    public ICommand MoveNoteDownCommand { get; }
    public ICommand AddToQueueCommand { get; }
    public ICommand RemoveFromQueueCommand { get; }
    public ICommand MoveQueueUpCommand { get; }
    public ICommand MoveQueueDownCommand { get; }
    public ICommand ToggleItemCommand { get; }
    public ICommand RefreshModelsCommand { get; }
    public ICommand UseCurrentFolderCommand { get; }
    public ICommand ReloadCommand { get; }

    public void ShowProject(ProjectProfile? project, string folder)
    {
        var same = _project?.Id == project?.Id;
        _project = project;
        _folder = folder;
        RebuildRows(same ? _selectedNote?.Id : null, same ? _selectedQueue?.Id : null);
        Feedback = _store.LoadWarning ?? (HasDrafts ? "Unsaved note drafts are kept while you switch projects. Use Save all drafts to keep them across restarts." : "Saving a note keeps it local. Add it to the queue when it is ready.");
    }

    private static string DraftKey(string projectId, string? noteId) => projectId + "/" + (noteId ?? "new");

    private void CaptureDraft()
    {
        if (_refreshingRows || _project == null) return;
        var key = DraftKey(_project.Id, _selectedNote?.Id);
        var original = _data.Notes.FirstOrDefault(x => x.Id == _selectedNote?.Id);
        if (_editorName == (original?.Name ?? "") && _editorPrompt == (original?.Prompt ?? ""))
        {
            _drafts.Remove(key);
            if (_selectedNote?.IsDraftOnly == true)
            {
                RebuildRows(null, _selectedQueue?.Id);
                return;
            }
        }
        else _drafts[key] = new(_project.Id, _selectedNote?.Id, _editorName, _editorPrompt);
        RefreshBindings();
    }

    private void LoadEditor()
    {
        var note = _data.Notes.FirstOrDefault(x => x.Id == _selectedNote?.Id);
        var draft = _project == null ? null : _drafts.GetValueOrDefault(DraftKey(_project.Id, _selectedNote?.Id));
        _editorName = draft?.Name ?? note?.Name ?? "";
        _editorPrompt = draft?.Prompt ?? note?.Prompt ?? "";
    }

    private void NewNote() { SelectedNote = null; Feedback = "Write a description. Leave the task name blank to use its first nonempty line."; }

    public bool SaveCurrentNote()
    {
        if (_project == null || !HasCurrentDraft) return true;
        var key = DraftKey(_project.Id, _selectedNote?.Id);
        string? savedId = null;
        if (!Commit(data => savedId = ApplyDraft(data, _drafts[key]), "Note saved. It will run only after execution is explicitly enabled in a future build.", rebuild: false)) return false;
        _drafts.Remove(key);
        RebuildRows(savedId, _selectedQueue?.Id);
        return true;
    }

    public bool HasDraftsForProject(string projectId) => _drafts.Values.Any(x => x.ProjectId == projectId);
    public bool SaveAllDrafts() => SaveDrafts(null);
    public bool SaveProjectDrafts(string projectId) => SaveDrafts(projectId);

    private bool SaveDrafts(string? projectId)
    {
        var drafts = _drafts.Where(x => projectId == null || x.Value.ProjectId == projectId).ToArray();
        if (drafts.Length == 0) return true;
        var selectedKey = _project == null ? null : DraftKey(_project.Id, _selectedNote?.Id);
        var selectedId = _selectedNote?.Id;
        if (!Commit(data =>
        {
            foreach (var (key, draft) in drafts)
            {
                var id = ApplyDraft(data, draft);
                if (key == selectedKey) selectedId = id;
            }
        }, "Note drafts saved locally.", rebuild: false)) return false;
        foreach (var (key, _) in drafts) _drafts.Remove(key);
        RebuildRows(selectedId, _selectedQueue?.Id);
        return true;
    }

    private static string ApplyDraft(ProjectTaskData data, NoteDraft draft)
    {
        var name = draft.Name.Trim();
        if (string.IsNullOrWhiteSpace(draft.Prompt)) throw new ArgumentException("Enter a description before saving the note.");
        if (name.Length == 0) name = draft.Prompt.Split('\n').Select(x => x.Trim()).First(x => x.Length > 0);
        if (name.Length > 160) name = name[..160];
        var note = draft.NoteId == null ? null : data.Notes.FirstOrDefault(x => x.Id == draft.NoteId && x.ProjectId == draft.ProjectId);
        // An explicit save of a recovered draft creates a new note if another
        // writer deleted its original. Reload never restores or saves it silently.
        if (note == null)
        {
            note = new() { ProjectId = draft.ProjectId, Order = data.Notes.Where(x => x.ProjectId == draft.ProjectId).Select(x => x.Order).DefaultIfEmpty(-1).Max() + 1 };
            data.Notes.Add(note);
        }
        note.Name = name;
        note.Prompt = draft.Prompt;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        return note.Id;
    }

    public void DiscardAllDrafts()
    {
        _drafts.Clear();
        RebuildRows(_selectedNote?.Id, _selectedQueue?.Id);
    }

    public void DiscardProjectDrafts(string projectId)
    {
        foreach (var key in _drafts.Where(x => x.Value.ProjectId == projectId).Select(x => x.Key).ToArray()) _drafts.Remove(key);
        RebuildRows(_selectedNote?.Id, _selectedQueue?.Id);
    }

    private void DiscardCurrentDraft()
    {
        if (_project == null) return;
        _drafts.Remove(DraftKey(_project.Id, _selectedNote?.Id));
        RebuildRows(_selectedNote?.Id, _selectedQueue?.Id);
    }

    private void DeleteNote()
    {
        if (_selectedNote is not { } note) return;
        if (note.IsDraftOnly)
        {
            _drafts.Remove(DraftKey(note.ProjectId, note.Id));
            RebuildRows(null, _selectedQueue?.Id);
            Feedback = "Recovered draft discarded.";
            return;
        }
        if (!Commit(data =>
        {
            data.Notes.RemoveAll(x => x.Id == note.Id);
            data.QueueItems.RemoveAll(x => x.NoteId == note.Id);
        }, "Note deleted and removed from the queue. Any execution receipts remain available.", rebuild: false)) return;
        _drafts.Remove(DraftKey(note.ProjectId, note.Id));
        RebuildRows(null, _selectedQueue?.Id);
    }

    private void ToggleComplete()
    {
        if (_selectedNote is not { } note) return;
        Commit(data =>
        {
            var entry = data.Notes.Single(x => x.Id == note.Id);
            entry.IsCompleted = !entry.IsCompleted;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            if (entry.IsCompleted)
                foreach (var item in data.QueueItems.Where(x => x.NoteId == note.Id)) item.Enabled = false;
        }, note.IsCompleted ? "Note reopened. Enable its queue item separately when ready." : "Note marked complete. Its queue item, if any, is disabled.");
    }

    private bool CanMoveNote(int direction) => CanEdit && SelectedNote != null && Notes.All(x => !x.IsDraftOnly) && Notes.IndexOf(SelectedNote) + direction >= 0 && Notes.IndexOf(SelectedNote) + direction < Notes.Count;
    private bool CanMoveQueue(int direction) => CanEdit && SelectedQueue != null && Queue.IndexOf(SelectedQueue) + direction >= 0 && Queue.IndexOf(SelectedQueue) + direction < Queue.Count;

    private void MoveNote(int direction)
    {
        if (!CanMoveNote(direction) || _selectedNote == null) return;
        var ids = Notes.Select(x => x.Id).ToList();
        var index = ids.IndexOf(_selectedNote.Id);
        (ids[index], ids[index + direction]) = (ids[index + direction], ids[index]);
        Commit(data => { for (var i = 0; i < ids.Count; i++) data.Notes.Single(x => x.Id == ids[i]).Order = i; }, "Note order saved. Queue order is separate.");
    }

    private void MoveQueue(int direction)
    {
        if (!CanMoveQueue(direction) || _selectedQueue == null) return;
        var ids = Queue.Select(x => x.Id).ToList();
        var index = ids.IndexOf(_selectedQueue.Id);
        (ids[index], ids[index + direction]) = (ids[index + direction], ids[index]);
        Commit(data => { for (var i = 0; i < ids.Count; i++) data.QueueItems.Single(x => x.Id == ids[i]).Order = i; }, "Queue order saved.");
    }

    private void AddToQueue()
    {
        if (_selectedNote == null || _project == null || HasCurrentDraft) return;
        string? queueId = null;
        if (!Commit(data =>
        {
            if (data.QueueItems.Any(x => x.NoteId == _selectedNote.Id)) throw new ArgumentException("This note is already in the queue.");
            if (!data.Queues.Any(x => x.ProjectId == _project.Id)) data.Queues.Add(new() { ProjectId = _project.Id, AssignedFolder = _folder });
            var model = Models.FirstOrDefault(x => x.IsDefault) ?? Models.FirstOrDefault();
            var item = new ProjectQueueItem
            {
                ProjectId = _project.Id, NoteId = _selectedNote.Id,
                Order = data.QueueItems.Where(x => x.ProjectId == _project.Id).Select(x => x.Order).DefaultIfEmpty(-1).Max() + 1,
                ModelId = model?.Id ?? "", ReasoningEffort = model?.DefaultReasoningEffort ?? ""
            };
            data.QueueItems.Add(item);
            queueId = item.Id;
        }, "Added to the paused queue. Select a model and thinking level for this item.", rebuild: false)) return;
        RebuildRows(_selectedNote.Id, queueId);
    }

    private void RemoveFromQueue()
    {
        if (_selectedQueue == null) return;
        var id = _selectedQueue.Id;
        Commit(data => data.QueueItems.RemoveAll(x => x.Id == id), "Removed from queue. The note and any execution receipts are retained.");
    }

    private void ToggleQueueItem()
    {
        if (_selectedQueue is not { } row) return;
        Commit(data =>
        {
            var item = data.QueueItems.Single(x => x.Id == row.Id);
            if (!item.Enabled && data.Notes.Single(x => x.Id == item.NoteId).IsCompleted)
                throw new ArgumentException("Reopen the completed note before enabling its queue item.");
            item.Enabled = !item.Enabled;
        }, row.Enabled ? "Item disabled. It stays in the queue." : "Item enabled. The queue remains paused.");
    }

    private void UseCurrentFolder()
    {
        if (_project == null || HasOutstandingRun) return;
        Commit(data =>
        {
            if (!Directory.Exists(_folder)) throw new ArgumentException("The current project folder does not exist. Update the project settings first.");
            var queue = data.Queues.Single(x => x.ProjectId == _project.Id);
            queue.Enabled = false;
            queue.AssignedFolder = _folder;
            queue.ExternalPredecessor = null;
        }, "Queue folder updated explicitly. Queue is paused and any external predecessor selection is cleared.");
    }

    private void SaveQueueOptions(string model, string effort)
    {
        if (_selectedQueue == null) return;
        if (!Models.Any(x => x.Id == model && x.SupportedReasoningEfforts.Any(e => e.Id == effort))) return;
        var id = _selectedQueue.Id;
        if (!Commit(data =>
        {
            var item = data.QueueItems.Single(x => x.Id == id);
            item.ModelId = model;
            item.ReasoningEffort = effort;
        }, "Model and thinking level saved for this queue item. They will be validated again before execution."))
        {
            LoadQueueOptions();
            RefreshBindings();
        }
    }

    private async Task RefreshModelsAsync()
    {
        if (_loadingModels) return;
        _loadingModels = true;
        ModelStatus = "Reading models from the installed Codex interface…";
        RefreshBindings();
        try
        {
            var models = await _catalog.LoadAsync(_lifetime.Token);
            if (_disposed) return;
            Models.Clear();
            foreach (var model in models) Models.Add(model);
            ModelStatus = $"{Models.Count} models loaded. Each thinking-level list comes from Codex.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (!_disposed)
            {
                Models.Clear();
                ModelStatus = ex is InvalidOperationException or TimeoutException ? ex.Message : "Models could not be read. Check the installed Codex CLI and try Refresh models again.";
            }
        }
        finally
        {
            _loadingModels = false;
            if (!_disposed) { LoadQueueOptions(); RefreshBindings(); }
        }
    }

    private void LoadQueueOptions()
    {
        _refreshingRows = true;
        try
        {
            _selectedModel = Models.FirstOrDefault(x => x.Id == _selectedQueue?.ModelId);
            Efforts.Clear();
            if (_selectedModel != null)
                foreach (var option in _selectedModel.SupportedReasoningEfforts) Efforts.Add(option);
            _selectedEffort = Efforts.FirstOrDefault(x => x.Id == _selectedQueue?.ReasoningEffort);
        }
        finally { _refreshingRows = false; }
    }

    private void Reload()
    {
        _data = _store.Load();
        RebuildRows(_selectedNote?.Id, _selectedQueue?.Id);
        Feedback = _store.LoadWarning ?? (HasDrafts
            ? "Reloaded saved notes; your unsaved drafts are retained. Review them before saving over newer edits. Drafts whose notes were deleted are recovered locally and save as new notes."
            : "Notes and queue reloaded from local storage.");
    }

    private bool Commit(Action<ProjectTaskData> change, string message, bool rebuild = true)
    {
        try
        {
            var candidate = ProjectTaskStore.Clone(_data);
            change(candidate);
            _store.Save(candidate);
            _data = candidate;
            if (rebuild) RebuildRows(_selectedNote?.Id, _selectedQueue?.Id);
            Feedback = message;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Feedback = ex is ArgumentException or InvalidOperationException ? ex.Message : "Changes were not saved. Check access to the task store, then reload or try saving again. Your draft is still here.";
            RefreshBindings();
            return false;
        }
    }

    private void RebuildRows(string? noteId, string? queueId)
    {
        _refreshingRows = true;
        try
        {
            Notes.Clear(); Queue.Clear();
            if (_project != null)
            {
                foreach (var note in _data.Notes.Where(x => x.ProjectId == _project.Id && !x.IsArchived).OrderBy(x => x.Order).ThenBy(x => x.CreatedAt))
                    Notes.Add(new(note.Id, note.ProjectId, note.Name, note.Prompt, note.IsCompleted, _data.QueueItems.Any(x => x.NoteId == note.Id)));
                foreach (var draft in _drafts.Values.Where(x => x.ProjectId == _project.Id && x.NoteId != null && !_data.Notes.Any(n => n.Id == x.NoteId)))
                    Notes.Add(new(draft.NoteId!, draft.ProjectId, string.IsNullOrWhiteSpace(draft.Name) ? "Recovered note draft" : draft.Name, draft.Prompt, false, false, true));
                foreach (var item in _data.QueueItems.Where(x => x.ProjectId == _project.Id).OrderBy(x => x.Order))
                {
                    var note = _data.Notes.FirstOrDefault(x => x.Id == item.NoteId);
                    Queue.Add(new(item.Id, item.NoteId, note?.Name ?? "Note unavailable", Queue.Count + 1, item.Enabled, item.ModelId, item.ReasoningEffort, item.State));
                }
            }
            _selectedNote = Notes.FirstOrDefault(x => x.Id == noteId);
            _selectedQueue = Queue.FirstOrDefault(x => x.Id == queueId);
        }
        finally { _refreshingRows = false; }
        LoadEditor();
        LoadQueueOptions();
        RefreshBindings();
    }

    private void RefreshBindings()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Dispose() { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
    private sealed record NoteDraft(string ProjectId, string? NoteId, string Name, string Prompt);
}

public sealed record NoteRow(string Id, string ProjectId, string Name, string Prompt, bool IsCompleted, bool IsQueued, bool IsDraftOnly = false)
{
    public string Bullet => IsCompleted ? "✓" : "•";
    public string Detail => IsDraftOnly ? "Recovered draft · save as a new note" : IsCompleted ? "Complete" : IsQueued ? "In queue" : "Note only";
    public string Preview => Prompt.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

public sealed record QueueRow(string Id, string NoteId, string Name, int Position, bool Enabled, string ModelId, string ReasoningEffort, ProjectQueueItemState State)
{
    public string Options => string.IsNullOrEmpty(ModelId) ? "Model and thinking level not selected" : $"{ModelId} · {ReasoningEffort}";
    public string Status => !Enabled ? "Disabled" : State == ProjectQueueItemState.Pending ? "Queued · paused" : State.ToString();
}

internal sealed class TaskPanelCommand(Action execute, Func<bool> canExecute) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter) { if (canExecute()) execute(); }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
