using System.Security.Cryptography;
using System.Text.Json;

namespace FullStackLauncher.ProjectTasks;

public sealed partial class ProjectTasksViewModel
{
    internal const string ReplacementResumeSection = "notes-workspace";
    private NoteEditorResumeState? _noteEditorResumeState;
    private bool _replacementOwnerRequestsSuspended;
    private int _ownerRequestCount;
    internal bool HasOwnerRequestInProgress => _ownerRequestCount > 0;
    internal void SuspendOwnerRequestsForReplacement() => _replacementOwnerRequestsSuspended = true;
    internal void ResumeOwnerRequestsAfterReplacement() => _replacementOwnerRequestsSuspended = false;
    public bool CanPrepareForDashboardReplacement => !_queueCommandBusy;

    internal NotesWorkspaceResumeState CaptureReplacementResumeState()
    {
        if (!CanPrepareForDashboardReplacement)
            throw new InvalidOperationException("Wait for the current notes or queue operation to finish before replacing Launcher.");
        CaptureDraft();
        return new()
        {
            ProjectId = _project?.Id,
            SelectedNoteId = _selectedNote?.Id,
            SelectedQueueId = _selectedQueue?.Id,
            SelectedReceiptId = _selectedReceipt?.Receipt.AttemptId,
            NotesPageIndex = _notesPageIndex,
            Editor = _noteEditorResumeState,
            NoteDrafts = _drafts.Values.Select(draft => new NotesDraftResumeState
            {
                ProjectId = draft.ProjectId, NoteId = draft.NoteId, Name = draft.Name, Prompt = draft.Prompt,
                Images = draft.Images, OriginalNoteFingerprint = draft.OriginalNoteFingerprint,
                OriginalNoteUpdatedAt = draft.OriginalNoteUpdatedAt, RecoveredFromNoteId = draft.RecoveredFromNoteId,
                OriginalAgentSource = draft.OriginalAgentSource, OriginalNoteCreatedAt = draft.OriginalNoteCreatedAt
            }).ToList(),
            QueueDelayDrafts = new(_queueDelayDrafts, StringComparer.Ordinal),
            AutomaticLoopDrafts = _automaticLoopDrafts.Select(pair => new NotesLoopDraftResumeState
            {
                ProjectId = pair.Key, Folder = pair.Value.Folder, ModelId = pair.Value.ModelId,
                Effort = pair.Value.Effort, AppGoal = pair.Value.AppGoal
            }).ToList()
        };
    }

    internal void RestoreReplacementResumeState(NotesWorkspaceResumeState state)
    {
        if (state.Version != 1 || state.NoteDrafts == null || state.QueueDelayDrafts == null ||
            state.AutomaticLoopDrafts == null || state.NoteDrafts.Any(saved => saved == null ||
                string.IsNullOrWhiteSpace(saved.ProjectId) || saved.Name == null || saved.Prompt == null || saved.Images == null) ||
            state.QueueDelayDrafts.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null) ||
            state.AutomaticLoopDrafts.Any(draft => draft == null || string.IsNullOrWhiteSpace(draft.ProjectId) ||
                draft.Folder == null || draft.ModelId == null || draft.Effort == null || draft.AppGoal == null))
            throw new InvalidOperationException("Saved notes workspace cannot be restored. Its checkpoint is retained for recovery.");

        var selectedNoteId = state.SelectedNoteId;
        var recovered = false;
        foreach (var saved in state.NoteDrafts)
        {
            var draft = new NoteDraft(saved.ProjectId, saved.NoteId, saved.Name, saved.Prompt, saved.Images,
                saved.OriginalNoteFingerprint, saved.OriginalNoteUpdatedAt, saved.RecoveredFromNoteId,
                saved.OriginalAgentSource, saved.OriginalNoteCreatedAt);
            var original = _data.Notes.FirstOrDefault(note => note.ProjectId == saved.ProjectId && note.Id == saved.NoteId);
            // A changed, archived or queued note stays intact. Retain its unsaved
            // editor text as a separate draft whose explicit Save creates a note.
            if (saved.NoteId != null && original != null &&
                (saved.OriginalNoteFingerprint == null || NoteFingerprint(original) != saved.OriginalNoteFingerprint ||
                 original.IsArchived || _data.QueueItems.Any(item => item.NoteId == original.Id && item.ProjectId == original.ProjectId)))
            {
                draft = DetachDraftForRecovery(draft);
                if (state.ProjectId == saved.ProjectId && selectedNoteId == saved.NoteId)
                    selectedNoteId = draft.NoteId;
                recovered = true;
            }
            // Preserve both versions if editing already resumed before restore.
            var key = DraftKey(draft.ProjectId, draft.NoteId);
            if (_drafts.TryGetValue(key, out var current) &&
                (current.Name != draft.Name || current.Prompt != draft.Prompt || !ImagesEqual(current.Images, draft.Images)))
            {
                draft = DetachDraftForRecovery(draft);
                if (state.ProjectId == saved.ProjectId && selectedNoteId == saved.NoteId)
                    selectedNoteId = draft.NoteId;
                recovered = true;
            }
            _drafts.TryAdd(DraftKey(draft.ProjectId, draft.NoteId), draft);
        }
        foreach (var pair in state.QueueDelayDrafts) _queueDelayDrafts.TryAdd(pair.Key, pair.Value);
        foreach (var draft in state.AutomaticLoopDrafts)
            _automaticLoopDrafts.TryAdd(draft.ProjectId, new(draft.Folder, draft.ModelId, draft.Effort, draft.AppGoal));
        _noteEditorResumeState = state.Editor;
        if (_noteEditorResumeState != null && _noteEditorResumeState.NoteId != selectedNoteId &&
            _noteEditorResumeState.ProjectId == state.ProjectId)
            _noteEditorResumeState = _noteEditorResumeState with { NoteId = selectedNoteId };
        if (_project?.Id == state.ProjectId)
        {
            _notesPageIndex = Math.Max(0, state.NotesPageIndex);
            RebuildRows(selectedNoteId, state.SelectedQueueId);
            _selectedReceipt = Receipts.FirstOrDefault(row => row.Receipt.AttemptId == state.SelectedReceiptId) ?? _selectedReceipt;
        }
        else RefreshBindings();
        Feedback = recovered
            ? "Drafts restored. Notes changed elsewhere are retained as separate recovered drafts; review and save them as new notes."
            : "Unfinished notes and queue settings restored. Review drafts and save when ready.";
        RefreshBindings();
    }

    internal void CaptureNoteEditorResumeState(NoteEditorResumeState state) => _noteEditorResumeState = state;
    internal NoteEditorResumeState? NoteEditorResumeState => _noteEditorResumeState;
    private static string? NoteFingerprint(ProjectTaskNote? note) => note == null ? null
        : Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(note)));

    private static NoteDraft DetachDraftForRecovery(NoteDraft draft) => draft with
    {
        // Stable IDs keep an identical restore retry from duplicating recovery rows.
        NoteId = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(draft)))[..32].ToLowerInvariant(),
        RecoveredFromNoteId = draft.RecoveredFromNoteId ?? draft.NoteId
    };

    private void RecoverConflictingNoteDrafts()
    {
        foreach (var pair in _drafts.ToArray())
        {
            var draft = pair.Value;
            if (draft.NoteId == null || draft.OriginalNoteFingerprint == null) continue;
            var note = _data.Notes.FirstOrDefault(note => note.Id == draft.NoteId && note.ProjectId == draft.ProjectId);
            if (note == null || NoteFingerprint(note) == draft.OriginalNoteFingerprint && !note.IsArchived &&
                !_data.QueueItems.Any(item => item.NoteId == note.Id && item.ProjectId == note.ProjectId)) continue;
            var recovered = DetachDraftForRecovery(draft);
            _drafts.Remove(pair.Key);
            _drafts.TryAdd(DraftKey(recovered.ProjectId, recovered.NoteId), recovered);
            if (_project?.Id == draft.ProjectId && _selectedNote?.Id == draft.NoteId)
                _selectedNote = new(recovered.NoteId!, recovered.ProjectId, recovered.Name, recovered.Prompt,
                    false, false, true, recovered.Images.Count);
        }
    }
}

public sealed record NotesWorkspaceResumeState
{
    public int Version { get; init; } = 1;
    public string? ProjectId { get; init; }
    public string? SelectedNoteId { get; init; }
    public string? SelectedQueueId { get; init; }
    public string? SelectedReceiptId { get; init; }
    public int NotesPageIndex { get; init; }
    public List<NotesDraftResumeState> NoteDrafts { get; init; } = [];
    public Dictionary<string, string> QueueDelayDrafts { get; init; } = new(StringComparer.Ordinal);
    public List<NotesLoopDraftResumeState> AutomaticLoopDrafts { get; init; } = [];
    public NoteEditorResumeState? Editor { get; init; }
    public NotesPanelResumeState? Panel { get; init; }
    public double Width { get; init; } = 1400;
    public double Height { get; init; } = 980;
    public double? Left { get; init; }
    public double? Top { get; init; }
    public bool Maximized { get; init; }
    public bool LargeEditorOpen { get; init; }
}

public sealed record NotesDraftResumeState
{
    public string ProjectId { get; init; } = "";
    public string? NoteId { get; init; }
    public string Name { get; init; } = "";
    public string Prompt { get; init; } = "";
    public List<ProjectTaskNoteImage> Images { get; init; } = [];
    public string? OriginalNoteFingerprint { get; init; }
    public DateTimeOffset? OriginalNoteUpdatedAt { get; init; }
    public string? RecoveredFromNoteId { get; init; }
    public AgentFollowUpNoteSource? OriginalAgentSource { get; init; }
    public DateTimeOffset? OriginalNoteCreatedAt { get; init; }
}

public sealed record NotesLoopDraftResumeState
{
    public string ProjectId { get; init; } = "";
    public string Folder { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string Effort { get; init; } = "";
    public string AppGoal { get; init; } = "";
}

public sealed record NoteEditorResumeState(string? ProjectId, string? NoteId,
    int SelectionStart, int SelectionLength, double VerticalOffset);
public sealed record NotesPanelResumeState(double VerticalOffset, bool QueueSettingsOpen);
