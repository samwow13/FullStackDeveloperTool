using System.Collections.ObjectModel;
using System.Windows.Input;

namespace FullStackLauncher.ProjectTasks;

public sealed partial class ProjectTasksViewModel
{
    private const int NotesPageSize = 10;
    private int _notesPageIndex;
    private string? _aiRetentionMessage;

    public ObservableCollection<NoteRow> PagedNotes { get; } = [];
    public ICommand PreviousNotesPageCommand { get; }
    public ICommand NextNotesPageCommand { get; }
    public ICommand RemoveAllAiPromptsCommand { get; }
    private int NotesPageCount => Math.Max(1, (Notes.Count + NotesPageSize - 1) / NotesPageSize);
    public string NotesPageLabel => Notes.Count == 0 ? "Page 1 of 1"
        : $"Page {_notesPageIndex + 1} of {NotesPageCount} · {_notesPageIndex * NotesPageSize + 1}–{Math.Min(Notes.Count, (_notesPageIndex + 1) * NotesPageSize)}";
    public int AiPromptCount => _project == null ? 0
        : _data.Notes.Count(note => note.ProjectId == _project.Id && note.AgentSource != null);
    public bool CanRemoveAiPrompts => CanEdit && AiPromptCount > 0;

    private ProjectTaskData LoadTaskData()
    {
        var data = _store.Load();
        _aiRetentionMessage = null;
        if (!_store.CanSave) return data;
        try
        {
            var result = _store.EnforceAiNoteLimit(data);
            if (result.RemovedCount > 0)
                _aiRetentionMessage = $"Removed {result.RemovedCount} oldest AI prompt(s). Each project keeps up to 20 AI prompts; active or unresolved queue attempts remain protected.";
            if (data.Notes.Where(note => note.AgentSource != null).GroupBy(note => note.ProjectId)
                .Any(notes => notes.Count() > ProjectAiNoteRetention.MaximumAiNotesPerProject))
                _aiRetentionMessage = "Some projects still exceed 20 AI prompts because active or unresolved queue attempts protect their notes. Finish or review those attempts, then reload to remove the oldest excess prompts.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            _aiRetentionMessage = ex is ArgumentException or InvalidOperationException ? ex.Message
                : "Old AI prompts could not be removed. Check access to the task store, then reload.";
        }
        return data;
    }

    private void RebuildNotesPage()
    {
        _notesPageIndex = Math.Clamp(_notesPageIndex, 0, NotesPageCount - 1);
        PagedNotes.Clear();
        foreach (var note in Notes.Skip(_notesPageIndex * NotesPageSize).Take(NotesPageSize))
            PagedNotes.Add(note);
    }

    private void ChangeNotesPage(int direction)
    {
        var next = _notesPageIndex + direction;
        if (next < 0 || next >= NotesPageCount) return;
        _refreshingRows = true;
        try
        {
            _notesPageIndex = next;
            _selectedNote = null;
            RebuildNotesPage();
        }
        finally { _refreshingRows = false; }
        LoadEditor();
        RefreshBindings();
    }

    private void RemoveAllAiPrompts()
    {
        if (!CanEdit || _project == null) return;
        var projectId = _project.Id;
        ProjectAiNoteRemovalResult? result = null;
        if (!Commit(data => result = ProjectAiNoteRetention.RemoveAll(data, projectId), "AI prompts removed.", rebuild: false)) return;
        foreach (var noteId in result!.RemovedNoteIds)
            _drafts.Remove(DraftKey(projectId, noteId));
        RebuildRows(_selectedNote?.Id, _selectedQueue?.Id);
        Feedback = $"Removed {result.RemovedCount} AI prompt(s). Manual notes and execution receipts retained." +
            (result.ProtectedCount == 0 ? "" : $" {result.ProtectedCount} AI prompt(s) retained for active or unresolved queue attempts; finish or review those attempts first.");
    }
}
