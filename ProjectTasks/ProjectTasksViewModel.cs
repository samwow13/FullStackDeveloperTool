using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FullStackLauncher.Models;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Local note editing and explicit controls for the independent queue owner.</summary>
public sealed class ProjectTasksViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ProjectTaskStore _store = new();
    private readonly CodexModelCatalog _catalog = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, NoteDraft> _drafts = new(StringComparer.Ordinal);
    private readonly Func<string, IReadOnlyList<ProjectApplicationState>>? _applicationStates;
    private ProjectTaskData _data;
    private ProjectProfile? _project;
    private string _folder = "";
    private NoteRow? _selectedNote;
    private QueueRow? _selectedQueue;
    private QueueReceiptRow? _selectedReceipt;
    private string _editorName = "";
    private string _editorPrompt = "";
    private string _feedback = "Saving a note keeps it local. Add it to the queue when it is ready.";
    private string _modelStatus = "Connecting to Codex to load model and thinking-level choices…";
    private bool _refreshingRows;
    private bool _loadingModels;
    private bool _disposed;
    private bool _queueCommandBusy;
    private DateTimeOffset _nextBlockedStoreReloadAt;
    private string _ownerMessage = "";
    private bool _ownerUnavailable;
    private bool _ownerCanStopCurrent;
    private string? _ownerActiveProjectId;
    private string? _ownerActiveAttemptId;
    private CodexModelOption? _selectedModel;
    private CodexReasoningEffortOption? _selectedEffort;
    private CodexModelOption? _selectedDefaultModel;
    private CodexReasoningEffortOption? _selectedDefaultEffort;

    public ProjectTasksViewModel(Func<string, IReadOnlyList<ProjectApplicationState>>? applicationStates = null)
    {
        _applicationStates = applicationStates;
        _data = _store.Load();
        if (_store.LoadWarning is { } warning) _feedback = warning;
        NewNoteCommand = new TaskPanelCommand(NewNote, () => CanEdit);
        SaveNoteCommand = new TaskPanelCommand(() => SaveCurrentNote(), () => CanEdit && HasCurrentDraft);
        SaveAndClearCommand = new TaskPanelCommand(SaveAndClear, () => CanEdit && HasCurrentDraft);
        SaveAndQueueCommand = new TaskPanelCommand(SaveAndQueue, () => CanSaveAndQueue);
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
        UseCodexDefaultCommand = new TaskPanelCommand(UseCodexDefault, () => CanChooseDefaultModel && Models.Any(x => x.IsDefault));
        EnableAutoRunCommand = new TaskPanelCommand(async () => await SendQueueCommandAsync("enable"), () => CanEnableAutoRun);
        PauseQueueCommand = new TaskPanelCommand(async () => await SendQueueCommandAsync("pause"), () => CanPauseQueue);
        ToggleGlobalPauseCommand = new TaskPanelCommand(async () => await SendQueueCommandAsync("pause-all", !_data.PauseAllQueues), () => _store.CanSave && !_queueCommandBusy);
        StopCurrentCommand = new TaskPanelCommand(async () => await SendQueueCommandAsync("stop"), () => CanStopCurrent);
        UseCurrentFolderCommand = new TaskPanelCommand(UseCurrentFolder, () => CanEdit && FolderChanged && !HasOutstandingRun);
        ClearPredecessorCommand = new TaskPanelCommand(ClearPredecessor, () => CanClearLegacyPredecessor);
        ReloadCommand = new TaskPanelCommand(Reload, () => true);
        InsertAppStatesCommand = new TaskPanelCommand(InsertAppStates, () => CanEdit && _applicationStates != null);
    }

    public ObservableCollection<NoteRow> Notes { get; } = [];
    public ObservableCollection<QueueRow> Queue { get; } = [];
    public ObservableCollection<QueueReceiptRow> Receipts { get; } = [];
    public ObservableCollection<TaskActivityRow> RecentTaskActivity { get; } = [];
    public ObservableCollection<TaskActivityRow> TaskHistory { get; } = [];
    public ObservableCollection<NoteImageRow> EditorImages { get; } = [];
    public ObservableCollection<CodexModelOption> Models { get; } = [];
    public ObservableCollection<CodexReasoningEffortOption> Efforts { get; } = [];
    public ObservableCollection<CodexReasoningEffortOption> DefaultEfforts { get; } = [];
    public string ProjectName => _project?.Name ?? "Choose a project in the dashboard";
    public string? ProjectId => _project?.Id;
    public string ProjectFolder => _project == null ? "Notes follow the selected launcher project." : _folder;
    public string StorePath => _store.StorePath;
    public bool CanEdit => _project != null && _store.CanSave;
    public bool HasProject => _project != null;
    public bool HasDrafts => _drafts.Count > 0;
    public bool HasCurrentDraft => _project != null && _drafts.ContainsKey(DraftKey(_project.Id, _selectedNote?.Id));
    private bool CanSaveAndQueue => CanEdit && HasCurrentDraft &&
        (_selectedNote == null || (!_selectedNote.IsCompleted && !_selectedNote.IsQueued));
    public bool HasSelectedNote => _selectedNote != null;
    public bool HasSelectedQueue => _selectedQueue != null;
    public bool CanChooseModel => CanEdit && HasSelectedQueue && Models.Count > 0 && !_loadingModels;
    public bool CanChooseDefaultModel => CanEdit && Models.Count > 0 && !_loadingModels;
    public bool CanEnableAutoRun => CanEdit && !_queueCommandBusy && _project != null &&
        !HasOutstandingRun && _data.Queues.All(x => x.ProjectId != _project.Id || !x.Enabled) &&
        Queue.Any(x => x.Enabled) && _data.Queues.Any(queue => queue.ProjectId == _project.Id &&
            Models.Any(model => model.Id == queue.DefaultModelId &&
                model.SupportedReasoningEfforts.Any(effort => effort.Id == queue.DefaultReasoningEffort)));
    public bool CanPauseQueue => CanEdit && !_queueCommandBusy &&
        _data.Queues.Any(x => x.ProjectId == _project?.Id && x.Enabled);
    public bool CanStopCurrent => !_queueCommandBusy && _project != null && _ownerCanStopCurrent &&
        _ownerActiveProjectId == _project.Id && !string.IsNullOrWhiteSpace(_ownerActiveAttemptId);
    public bool HasLegacyPredecessor => _data.Queues.Any(x => x.ProjectId == _project?.Id && x.ExternalPredecessor != null);
    private bool CanClearLegacyPredecessor => CanEdit && !_queueCommandBusy && !HasOutstandingRun &&
        _data.Queues.Any(x => x.ProjectId == _project?.Id && !x.Enabled && x.ExternalPredecessor != null);
    public ProjectTaskExecutionReceipt? HeldAttempt => _project == null ? null :
        _data.Receipts.Where(x => x.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
            x.Snapshot.ProjectId == _project.Id && x.QueueReviewCompletedAt == null &&
            x.QueueAbandonedAt == null &&
            !IsConfirmedSuccessfulReceipt(x) &&
            (x.State is ProjectTaskRunState.Recovering or ProjectTaskRunState.NeedsAttention or
                ProjectTaskRunState.Failed or ProjectTaskRunState.Interrupted or ProjectTaskRunState.Completed))
            .OrderBy(x => x.CreatedAt).FirstOrDefault();
    public bool CanReleaseHeldAttempt => !_queueCommandBusy && _store.CanSave && HeldAttempt != null;
    public bool HasHeldAttempt => HeldAttempt != null;
    public ProjectTaskExecutionReceipt? SelectedRetryReceipt => _selectedQueue == null ? null :
        _data.Receipts.FirstOrDefault(x => x.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
            x.AttemptId == _data.QueueItems.FirstOrDefault(item => item.Id == _selectedQueue.Id)?.LastAttemptId);
    public bool CanRetrySelectedItem => CanEdit && !_queueCommandBusy && _selectedQueue is { } selected &&
        (selected.State is ProjectQueueItemState.Failed or ProjectQueueItemState.Interrupted or ProjectQueueItemState.NeedsAttention) &&
        SelectedRetryReceipt is { } receipt &&
        receipt.QueueReviewCompletedAt != null && receipt.QueueAbandonedAt == null &&
        !IsConfirmedSuccessfulReceipt(receipt);
    public string HeldAttemptSummary => HeldAttempt != null
        ? "A previous queue attempt needs review before this queue can run. Its item stays skipped after release."
        : "";
    public bool HasOutstandingRun => _data.Receipts.Any(x => x.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
        x.Snapshot.ProjectId == _project?.Id && x.QueueReviewCompletedAt == null &&
        x.QueueAbandonedAt == null &&
        !IsConfirmedSuccessfulReceipt(x) &&
        (x.State is ProjectTaskRunState.Prepared or ProjectTaskRunState.Starting or ProjectTaskRunState.Running or
            ProjectTaskRunState.Recovering or ProjectTaskRunState.NeedsAttention or
            ProjectTaskRunState.Failed or ProjectTaskRunState.Interrupted or ProjectTaskRunState.Completed))
        || Queue.Any(x => x.State is ProjectQueueItemState.Starting or ProjectQueueItemState.Running or ProjectQueueItemState.Recovering);
    public bool FolderChanged => _project != null && _data.Queues.FirstOrDefault(x => x.ProjectId == _project.Id) is { } queue && !queue.AssignedFolder.Equals(_folder, StringComparison.OrdinalIgnoreCase);
    public string AssignedFolder => _data.Queues.FirstOrDefault(x => x.ProjectId == _project?.Id)?.AssignedFolder ?? _folder;
    public string QueueStatus => _project == null
        ? "Select a project to prepare its queue."
        : !string.IsNullOrEmpty(_ownerMessage) ? _ownerMessage
        : _ownerUnavailable && (_data.Queues.Any(x => x.ProjectId == _project.Id && x.Enabled) || HasOutstandingRun)
            ? "Queue owner unavailable. Enabled intent is saved, but no new task can start until the owner reconnects."
        : HasOutstandingRun ? HeldAttempt != null
            ? "A queue attempt needs attention. Review the held attempt before enabling the queue."
            : "A queue task is running or recovering. Wait for its exact turn status before enabling the queue."
        : _data.PauseAllQueues ? "All queues paused. Current task, if any, continues."
        : _data.Queues.Any(x => x.ProjectId == _project.Id && x.Enabled)
            ? "Queue enabled. The independent queue owner selects eligible items in saved order."
            : "Queue paused. Use Enable Queue to submit eligible saved items.";
    public string GlobalPauseLabel => _data.PauseAllQueues ? "Clear global pause" : "Pause all queues";
    public string GlobalPauseStatus => _data.PauseAllQueues
        ? "All queues are paused. Clear global pause here, then enable this queue if needed. Other enabled queues may resume when the global pause clears."
        : "";
    public string NoteCount => $"{Notes.Count} notes · {Notes.Count(x => x.IsCompleted)} complete";
    public string QueueCount => $"{Queue.Count} items · {Queue.Count(x => x.Enabled)} enabled";
    public string ReceiptCount => $"{Receipts.Count} saved queue attempts";
    public string ActivitySummary { get; private set; } = "Select a project to see task progress.";
    public string HistorySummary => TaskHistory.Count == 0
        ? "No tasks in History. Confirmed completed tasks move here after 10 minutes."
        : $"{TaskHistory.Count} task{(TaskHistory.Count == 1 ? "" : "s")} in History. Saved execution receipts remain in All attempts.";
    public string HistoryButtonLabel => TaskHistory.Count == 0 ? "History…" : $"History ({TaskHistory.Count})…";
    public bool CanManageTaskHistory => _project is not null && _store.CanSave && !_queueCommandBusy;
    public string SelectedReceiptDetails => _selectedReceipt is not { Receipt: { } receipt }
        ? "Select an attempt to inspect its exact task and turn IDs, outcome, and saved result."
        : $"Attempt: {receipt.AttemptId}\nTask: {receipt.ThreadId ?? "unknown"}\nTurn: {receipt.TurnId ?? "unknown"}\n" +
          $"State: {receipt.State} · Outcome: {receipt.Outcome}\n" +
          $"Started: {receipt.StartedAt?.ToLocalTime().ToString("g") ?? "not confirmed"} · Finished: {receipt.FinishedAt?.ToLocalTime().ToString("g") ?? "not confirmed"}\n" +
          $"Summary: {receipt.ResultSummary}\nAttention: {receipt.AttentionReason}\nCompletion message: {receipt.CompletionMessage}";
    public string SelectedReceiptFinalResponse => _selectedReceipt?.Receipt.FinalResponse ?? "";
    private static bool IsConfirmedSuccessfulReceipt(ProjectTaskExecutionReceipt receipt) =>
        receipt.FinishedAt is not null && receipt.State == ProjectTaskRunState.Completed &&
        receipt.Outcome == ProjectTaskOutcome.Succeeded &&
        !string.IsNullOrWhiteSpace(receipt.ThreadId) && !string.IsNullOrWhiteSpace(receipt.TurnId);
    public string DraftStatus => HasDrafts ? $"{_drafts.Count} unsaved draft(s) across projects. Save all drafts before leaving." : "All note changes saved locally.";
    public string EditorHeading => SelectedNote == null ? "New note" : "Edit note";
    public string EditorImageCount => $"{EditorImages.Count} / {ProjectTaskNoteImage.MaximumCount} images";
    public string CompleteLabel => SelectedNote?.IsCompleted == true ? "Reopen note" : "Mark complete";
    public string ItemEnabledLabel => SelectedQueue?.Enabled == true ? "Disable item" : "Enable item";
    public string Feedback { get => _feedback; private set { _feedback = value; Changed(); } }
    public string ModelStatus { get => _modelStatus; private set { _modelStatus = value; Changed(); } }
    public string SavedQueueOptions => SelectedQueue == null ? "Select an item to configure it." : $"Saved: {SelectedQueue.Options}";
    public string DefaultOptionsStatus
    {
        get
        {
            if (_project == null) return "Select a project to choose its Codex model.";
            var queue = _data.Queues.FirstOrDefault(x => x.ProjectId == _project.Id);
            if (queue == null || string.IsNullOrWhiteSpace(queue.DefaultModelId) || string.IsNullOrWhiteSpace(queue.DefaultReasoningEffort))
                return "Choose a model and thinking level before enabling the queue.";
            if (Models.Count > 0 && !Models.Any(x => x.Id == queue.DefaultModelId &&
                x.SupportedReasoningEfforts.Any(e => e.Id == queue.DefaultReasoningEffort)))
                return "Saved model or thinking level is unavailable in Codex. Choose another selection.";
            return Models.Count == 0
                ? $"Saved selection: {queue.DefaultModelId} · {queue.DefaultReasoningEffort}. Connect to Codex to validate it."
                : $"Saved selection: {queue.DefaultModelId} · {queue.DefaultReasoningEffort}. Future queue runs use this choice.";
        }
    }
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

    public QueueReceiptRow? SelectedReceipt
    {
        get => _selectedReceipt;
        set
        {
            if (_refreshingRows || ReferenceEquals(_selectedReceipt, value)) return;
            _selectedReceipt = value;
            Changed();
            Changed(nameof(SelectedReceiptDetails));
            Changed(nameof(SelectedReceiptFinalResponse));
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

    public CodexModelOption? SelectedDefaultModel
    {
        get => _selectedDefaultModel;
        set
        {
            if (_refreshingRows || value == null || _project == null || _selectedDefaultModel?.Id == value.Id) return;
            var effort = value.SupportedReasoningEfforts.FirstOrDefault(x => x.Id == value.DefaultReasoningEffort);
            if (effort != null) SaveQueueDefaults(value.Id, effort.Id);
        }
    }

    public CodexReasoningEffortOption? SelectedDefaultEffort
    {
        get => _selectedDefaultEffort;
        set
        {
            if (_refreshingRows || value == null || _selectedDefaultModel == null || _project == null || _selectedDefaultEffort?.Id == value.Id) return;
            SaveQueueDefaults(_selectedDefaultModel.Id, value.Id);
        }
    }

    public ICommand NewNoteCommand { get; }
    public ICommand SaveNoteCommand { get; }
    public ICommand SaveAndClearCommand { get; }
    public ICommand SaveAndQueueCommand { get; }
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
    public ICommand UseCodexDefaultCommand { get; }
    public ICommand EnableAutoRunCommand { get; }
    public ICommand PauseQueueCommand { get; }
    public ICommand ToggleGlobalPauseCommand { get; }
    public ICommand StopCurrentCommand { get; }
    public ICommand UseCurrentFolderCommand { get; }
    public ICommand ClearPredecessorCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand InsertAppStatesCommand { get; }

    public void ShowProject(ProjectProfile? project, string folder)
    {
        var same = _project?.Id == project?.Id;
        _project = project;
        _folder = folder;
        _ownerMessage = "";
        _ownerUnavailable = false;
        _ownerCanStopCurrent = false;
        _ownerActiveProjectId = null;
        _ownerActiveAttemptId = null;
        RebuildRows(same ? _selectedNote?.Id : null, same ? _selectedQueue?.Id : null);
        Feedback = _store.LoadWarning ?? (HasDrafts ? "Unsaved note drafts are kept while you switch projects. Use Save all drafts to keep them across restarts." : "Saving a note keeps it local. Add it to the queue when it is ready.");
    }

    private static string DraftKey(string projectId, string? noteId) => projectId + "/" + (noteId ?? "new");

    private void CaptureDraft()
    {
        if (_refreshingRows || _project == null) return;
        var key = DraftKey(_project.Id, _selectedNote?.Id);
        var original = _data.Notes.FirstOrDefault(x => x.Id == _selectedNote?.Id);
        var images = EditorImages.Select(x => x.ToModel()).ToList();
        if (_editorName == (original?.Name ?? "") && _editorPrompt == (original?.Prompt ?? "") &&
            ImagesEqual(images, original?.Images ?? []))
        {
            _drafts.Remove(key);
            if (_selectedNote?.IsDraftOnly == true)
            {
                RebuildRows(null, _selectedQueue?.Id);
                return;
            }
        }
        else _drafts[key] = new(_project.Id, _selectedNote?.Id, _editorName, _editorPrompt, images);
        RefreshBindings();
    }

    private static bool ImagesEqual(IReadOnlyList<ProjectTaskNoteImage> left, IReadOnlyList<ProjectTaskNoteImage> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Id == pair.Second.Id &&
            pair.First.Caption == pair.Second.Caption && pair.First.MimeType == pair.Second.MimeType &&
            pair.First.DataBase64 == pair.Second.DataBase64 &&
            pair.First.PageUrl == pair.Second.PageUrl && pair.First.PageHtml == pair.Second.PageHtml &&
            pair.First.PageCss == pair.Second.PageCss && pair.First.PageCaptureStatus == pair.Second.PageCaptureStatus &&
            pair.First.IncludePageContextInPrompt == pair.Second.IncludePageContextInPrompt);

    private void LoadEditor()
    {
        var note = _data.Notes.FirstOrDefault(x => x.Id == _selectedNote?.Id);
        var draft = _project == null ? null : _drafts.GetValueOrDefault(DraftKey(_project.Id, _selectedNote?.Id));
        _editorName = draft?.Name ?? note?.Name ?? "";
        _editorPrompt = draft?.Prompt ?? note?.Prompt ?? "";
        foreach (var oldImage in EditorImages) oldImage.PropertyChanged -= EditorImageChanged;
        EditorImages.Clear();
        foreach (var image in draft?.Images ?? note?.Images ?? [])
        {
            var row = new NoteImageRow(image);
            row.PropertyChanged += EditorImageChanged;
            EditorImages.Add(row);
        }
    }

    private void EditorImageChanged(object? sender, PropertyChangedEventArgs e) => CaptureDraft();

    internal void AddImage(byte[] bytes, string mimeType, string? caption = null, BrowserPageContext? pageContext = null)
    {
        if (!CanEdit) return;
        if (bytes.Length == 0 || bytes.Length > ProjectTaskNoteImage.MaximumBytes ||
            mimeType is not ("image/png" or "image/jpeg"))
        {
            Feedback = $"Image must be PNG or JPEG and no larger than {ProjectTaskNoteImage.MaximumBytes / 1_000_000} MB.";
            return;
        }
        if (EditorImages.Count >= ProjectTaskNoteImage.MaximumCount ||
            EditorImages.Sum(x => x.ByteCount) + bytes.Length > ProjectTaskNoteImage.MaximumTotalBytes)
        {
            Feedback = "This note has reached its image count or 24 MB image limit.";
            return;
        }
        var label = caption?.Trim();
        var pageHtml = LimitPageText(pageContext?.Html, ProjectTaskNoteImage.MaximumPageHtmlCharacters);
        var pageCss = LimitPageText(pageContext?.Css, ProjectTaskNoteImage.MaximumPageCssCharacters);
        var row = new NoteImageRow(new ProjectTaskNoteImage
        {
            Caption = string.IsNullOrEmpty(label) ? $"Image {EditorImages.Count + 1}" : label[..Math.Min(label.Length, 240)], MimeType = mimeType,
            DataBase64 = Convert.ToBase64String(bytes),
            PageUrl = LimitPageText(pageContext?.Url, ProjectTaskNoteImage.MaximumPageUrlCharacters),
            PageHtml = pageHtml,
            PageCss = pageCss,
            PageCaptureStatus = LimitPageText(pageContext?.Status, ProjectTaskNoteImage.MaximumPageCaptureStatusCharacters),
            IncludePageContextInPrompt = !string.IsNullOrWhiteSpace(pageHtml) || !string.IsNullOrWhiteSpace(pageCss)
        });
        row.PropertyChanged += EditorImageChanged;
        EditorImages.Add(row);
        CaptureDraft();
        Feedback = pageContext == null ? "Image added to the unsaved note draft. Add a caption, then save the note."
            : row.HasPageSource ? "Snip and browser page source added to the unsaved draft. Review the source option, then save."
            : "Snip added to the unsaved draft. Browser page source was unavailable; see the image status below.";
    }

    private static string LimitPageText(string? value, int maximum) =>
        value == null ? "" : value.Length <= maximum ? value : value[..maximum];

    public void RemoveImage(string id)
    {
        var row = EditorImages.FirstOrDefault(x => x.Id == id);
        if (row == null || !CanEdit) return;
        row.PropertyChanged -= EditorImageChanged;
        EditorImages.Remove(row);
        CaptureDraft();
        Feedback = "Image removed from the draft. Save the note to keep this change.";
    }

    public void ReportImageError(string message) => Feedback = message;

    private void InsertAppStates()
    {
        if (_project == null || _applicationStates == null) return;
        IReadOnlyList<ProjectApplicationState> states;
        try { states = _applicationStates(_project.Id); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Feedback = "Current application states are unavailable. Refresh the dashboard and try again.";
            return;
        }
        var text = new StringBuilder();
        text.AppendLine($"Application states at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ({_project.Name}):");
        if (states.Count == 0) text.AppendLine("- No applications are configured for this project.");
        foreach (var state in states)
        {
            text.Append("- ").Append(state.Name).Append(" (").Append(state.Kind).Append("): ").Append(state.State);
            if (!string.IsNullOrWhiteSpace(state.ActiveUrl)) text.Append(" · ").Append(state.ActiveUrl);
            if (!string.IsNullOrWhiteSpace(state.Detail))
                text.Append(" · ").Append(state.Detail.Replace('\r', ' ').Replace('\n', ' '));
            text.AppendLine();
        }
        EditorPrompt = string.IsNullOrWhiteSpace(EditorPrompt) ? text.ToString() : EditorPrompt.TrimEnd() + Environment.NewLine + Environment.NewLine + text;
        Feedback = "Current launcher application states inserted into the unsaved draft. Review before saving.";
    }

    private void NewNote() { SelectedNote = null; Feedback = "Write a description. Leave the task name blank to use its first nonempty line."; }

    public bool SaveCurrentNote()
    {
        if (_project == null || !HasCurrentDraft) return true;
        var key = DraftKey(_project.Id, _selectedNote?.Id);
        string? savedId = null;
        if (!Commit(data => savedId = ApplyDraft(data, _drafts[key]),
            "Note saved locally. An enabled queue may start a pending item independently.", rebuild: false)) return false;
        _drafts.Remove(key);
        RebuildRows(savedId, _selectedQueue?.Id);
        return true;
    }

    private void SaveAndClear()
    {
        if (!CanEdit || !HasCurrentDraft || !SaveCurrentNote()) return;
        SelectedNote = null;
        Feedback = "Note saved locally. Ready for another note.";
    }

    private void SaveAndQueue()
    {
        if (!CanSaveAndQueue || _project == null) return;
        var projectId = _project.Id;
        var folder = _folder;
        var key = DraftKey(projectId, _selectedNote?.Id);
        var draft = _drafts[key];
        string? savedId = null;
        string? queueId = null;
        if (!Commit(data =>
        {
            var noteId = ApplyDraft(data, draft);
            savedId = noteId;
            queueId = AddQueueItem(data, projectId, noteId, folder);
        }, "Note saved and added to the queue. An enabled queue may select it immediately.", rebuild: false)) return;
        _drafts.Remove(key);
        RebuildRows(savedId, queueId);
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
        note.Images = draft.Images.Select(image => new ProjectTaskNoteImage
        {
            Id = image.Id, Caption = image.Caption, MimeType = image.MimeType, DataBase64 = image.DataBase64,
            PageUrl = image.PageUrl, PageHtml = image.PageHtml, PageCss = image.PageCss,
            PageCaptureStatus = image.PageCaptureStatus,
            IncludePageContextInPrompt = image.IncludePageContextInPrompt
        }).ToList();
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
        if (!Commit(data => queueId = AddQueueItem(data, _project.Id, _selectedNote.Id, _folder),
            "Added to the queue. It will use the saved Codex model when dispatched. An enabled queue may select it immediately.", rebuild: false)) return;
        RebuildRows(_selectedNote.Id, queueId);
    }

    private static string AddQueueItem(ProjectTaskData data, string projectId, string noteId, string folder)
    {
        var note = data.Notes.FirstOrDefault(x => x.Id == noteId && x.ProjectId == projectId);
        if (note == null) throw new ArgumentException("Save the note before adding it to the queue.");
        if (note.IsCompleted) throw new ArgumentException("Reopen the completed note before adding it to the queue.");
        if (data.QueueItems.Any(x => x.NoteId == noteId)) throw new ArgumentException("This note is already in the queue.");
        if (!data.Queues.Any(x => x.ProjectId == projectId))
            data.Queues.Add(new() { ProjectId = projectId, AssignedFolder = folder });
        var item = new ProjectQueueItem
        {
            ProjectId = projectId, NoteId = noteId,
            Order = data.QueueItems.Where(x => x.ProjectId == projectId).Select(x => x.Order).DefaultIfEmpty(-1).Max() + 1
        };
        data.QueueItems.Add(item);
        return item.Id;
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
        }, row.Enabled ? "Item disabled. It stays in the queue." : "Item enabled. An enabled queue may select it immediately.");
    }

    public Task ReleaseHeldAttemptAsync(string attemptId) => SendQueueCommandAsync("review-release",
        attemptId: attemptId, confirmedNoActiveTask: true);

    public Task RetrySelectedItemAsync(string itemId, string expectedAttemptId) => SendQueueCommandAsync("retry-item",
        attemptId: expectedAttemptId, itemId: itemId, confirmedRetry: true);

    public bool CanStopTask(string attemptId) => CanStopCurrent &&
        string.Equals(_ownerActiveAttemptId, attemptId, StringComparison.Ordinal);

    public Task StopTaskAsync(string attemptId)
    {
        if (CanStopTask(attemptId)) return SendQueueCommandAsync("stop", attemptId: attemptId);
        Feedback = "This exact attempt is no longer confirmed active. Refresh task status before stopping.";
        return Task.CompletedTask;
    }

    public bool CanRetryTask(string attemptId) => !_queueCommandBusy && CanEdit &&
        _data.Receipts.FirstOrDefault(receipt => receipt.AttemptId == attemptId) is { } receipt &&
        receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
        receipt.Snapshot.ProjectId == _project?.Id &&
        receipt.QueueReviewCompletedAt is not null && receipt.QueueAbandonedAt is null &&
        !IsConfirmedSuccessfulReceipt(receipt) &&
        _data.QueueItems.Any(item => item.Id == receipt.Snapshot.QueueItemId &&
            item.ProjectId == _project.Id && item.LastAttemptId == attemptId &&
            item.State is ProjectQueueItemState.Failed or ProjectQueueItemState.Interrupted or ProjectQueueItemState.NeedsAttention);

    public Task RetryTaskAsync(string attemptId)
    {
        if (!CanRetryTask(attemptId))
        {
            Feedback = "This exact attempt is no longer ready to re-attempt. Review its latest status.";
            return Task.CompletedTask;
        }
        var itemId = _data.Receipts.Single(receipt => receipt.AttemptId == attemptId).Snapshot.QueueItemId;
        return SendQueueCommandAsync("retry-item", attemptId: attemptId, itemId: itemId, confirmedRetry: true);
    }

    public bool MoveTaskToHistory(string attemptId) => SaveActivityChange(data =>
    {
        var receipt = data.Receipts.SingleOrDefault(candidate => candidate.AttemptId == attemptId &&
            candidate.Purpose == ProjectTaskExecutionPurpose.QueueItem && candidate.Snapshot.ProjectId == _project?.Id)
            ?? throw new ArgumentException("This task attempt is no longer available.");
        if (receipt.ActivityDeletedAt is not null || receipt.ActivityArchivedAt is not null ||
            !CanArchiveReceipt(receipt))
            throw new InvalidOperationException("Finish or review this exact attempt before moving it to History.");
        receipt.ActivityArchivedAt = DateTimeOffset.UtcNow;
        return 1;
    }, "Task moved to History. Its execution receipt remains in All attempts.");

    public async Task<bool> DeleteTaskActivityAsync(string projectId, string attemptId)
    {
        if (_queueCommandBusy || !_store.CanSave)
        {
            Feedback = _store.LoadWarning ?? "Task activity cannot be deleted while another queue command is running.";
            return false;
        }
        _queueCommandBusy = true;
        RefreshBindings();
        try
        {
            await QueueOwnerClient.EnsureCompatibleOwnerAsync(_store.StorePath, _lifetime.Token);
            var reply = await QueueOwnerClient.TryDeleteAttemptAsync(_store.StorePath,
                projectId, attemptId, _lifetime.Token);
            var data = reply is null
                ? await Task.Run(() => ProjectQueueCoordinator.AbandonAttemptInStore(
                    _store, projectId, attemptId))
                : await Task.Run(() => _store.Load());
            if (_disposed) return false;
            if (!_store.CanSave)
            {
                Feedback = reply is { Success: true }
                    ? "The queue owner saved Delete, but the task store could not be refreshed. Reload to inspect the saved receipt."
                    : _store.LoadWarning ?? "Task activity could not be refreshed.";
                return false;
            }
            if (!data.Receipts.Any(receipt => receipt.AttemptId == attemptId &&
                    receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                    receipt.Snapshot.ProjectId == projectId && receipt.ActivityDeletedAt is not null))
            {
                Feedback = reply is { Success: true }
                    ? "The queue owner reported Delete, but the saved receipt is unchanged. Reload and retry."
                    : reply?.Message ?? "Task deletion was not saved. Reload and retry.";
                return false;
            }
            _data = data;
            RebuildRows(_selectedNote?.Id, _selectedQueue?.Id);
            var abandoned = data.Receipts.Any(receipt => receipt.AttemptId == attemptId &&
                receipt.Snapshot.ProjectId == projectId && receipt.QueueAbandonedAt is not null);
            Feedback = abandoned
                ? "Task deleted from tracking. Its attempt will not retry. Queues are paused until you resume them; the saved outcome remains available in All attempts."
                : "Task deleted from tracking. Its saved receipt and outcome remain available in All attempts.";
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or System.Text.Json.JsonException or Win32Exception)
        {
            if (!_disposed)
            {
                Reload();
                Feedback = ex is ArgumentException or InvalidOperationException ||
                    ex is IOException && (ex.Message.StartsWith("An older queue owner", StringComparison.Ordinal) ||
                        ex.Message.StartsWith("The queue owner is running", StringComparison.Ordinal)) ? ex.Message :
                    "Task deletion was not saved. Check access to the task store, then reload and retry.";
            }
            return false;
        }
        finally
        {
            _queueCommandBusy = false;
            if (!_disposed) RefreshBindings();
        }
    }

    public bool ClearTaskHistory()
    {
        if (_project is not { } project) return false;
        var projectId = project.Id;
        return SaveActivityChange(data =>
        {
            var latestIds = LatestReceiptIds(data, projectId);
            var now = DateTimeOffset.UtcNow;
            var cleared = 0;
            foreach (var receipt in data.Receipts.Where(receipt => receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                         receipt.Snapshot.ProjectId == projectId && receipt.ActivityDeletedAt is null &&
                         IsArchivedForDisplay(receipt, latestIds, data.QueueItems, now)))
            {
                if (receipt.ActivityArchivedAt is null && !IsConfirmedSuccessfulReceipt(receipt))
                    receipt.ActivityArchivedAt = now;
                receipt.ActivityDeletedAt = now;
                cleared++;
            }
            return cleared;
        }, "History cleared. Saved execution receipts remain in All attempts.");
    }

    private bool SaveActivityChange(Func<ProjectTaskData, int> change, string message)
    {
        if (!CanManageTaskHistory)
        {
            Feedback = _store.LoadWarning ?? "Task History is unavailable while the queue is busy or no project is selected.";
            return false;
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var latest = _store.Load();
            if (!_store.CanSave)
            {
                Feedback = _store.LoadWarning ?? "Task activity could not be loaded. Nothing was changed.";
                return false;
            }
            _data = latest;
            var candidate = ProjectTaskStore.Clone(latest);
            try
            {
                if (change(candidate) == 0)
                {
                    RebuildRows(_selectedNote?.Id, _selectedQueue?.Id);
                    Feedback = "No task activity needed changing.";
                    return false;
                }
                _store.Save(candidate);
                _data = candidate;
                RebuildRows(_selectedNote?.Id, _selectedQueue?.Id);
                Feedback = message;
                return true;
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith(
                "Project tasks changed in another launcher or editor.", StringComparison.Ordinal) && attempt < 2)
            {
                // The queue owner can write a receipt between this read and save.
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                RebuildRows(_selectedNote?.Id, _selectedQueue?.Id);
                Feedback = ex is ArgumentException or InvalidOperationException ? ex.Message :
                    "Task activity was not saved. Check access to the task store, then reload and retry.";
                return false;
            }
        }
        Reload();
        Feedback = "Task activity changed in another launcher or editor. Reloaded; try again.";
        return false;
    }

    private async Task SendQueueCommandAsync(string command, bool? paused = null,
        string? attemptId = null, bool confirmedNoActiveTask = false,
        string? itemId = null, bool confirmedRetry = false)
    {
        if (_queueCommandBusy || (command != "pause-all" && _project == null)) return;
        if (command == "stop")
        {
            if (attemptId is not null && !CanStopTask(attemptId))
            {
                Feedback = "This exact attempt is no longer confirmed active. Refresh task status before stopping.";
                return;
            }
            attemptId ??= _ownerActiveAttemptId;
        }
        _queueCommandBusy = true;
        Feedback = "Contacting the independent queue owner. Crash recovery may take up to two minutes; no new command has been applied yet.";
        RefreshBindings();
        try
        {
            var reply = await QueueOwnerClient.SendAsync(_store.StorePath, command,
                _project?.Id, paused, startIfMissing: true, _lifetime.Token,
                attemptId, confirmedNoActiveTask, itemId, confirmedRetry);
            if (_disposed) return;
            Reload();
            _ownerMessage = reply.Message;
            _ownerUnavailable = !reply.Success;
            _ownerCanStopCurrent = reply.CanStopCurrent;
            _ownerActiveProjectId = reply.ActiveProjectId;
            _ownerActiveAttemptId = reply.ActiveAttemptId;
            Feedback = reply.Message;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (!_disposed) Feedback = ex.Message;
        }
        finally
        {
            _queueCommandBusy = false;
            if (!_disposed) RefreshBindings();
        }
    }

    public async Task RefreshOwnerStatusAsync()
    {
        if (_disposed || _queueCommandBusy) return;
        // A transient file read failure blocks saves until Load succeeds. Keep
        // the existing file and drafts intact while retrying at a bounded rate.
        if (!_store.CanSave && DateTimeOffset.UtcNow >= _nextBlockedStoreReloadAt)
        {
            _nextBlockedStoreReloadAt = DateTimeOffset.UtcNow.AddSeconds(15);
            Reload();
        }
        if (_project == null) return;
        var projectId = _project.Id;
        try
        {
            // The owner saves queue transitions in another process. Check off the UI
            // thread, then reload only changed files so rows and the save baseline
            // stay current without rebuilding the editor on every status poll.
            if (_store.CanSave && await Task.Run(() => _store.HasChangedSinceLoad(), _lifetime.Token))
            {
                if (_disposed || _queueCommandBusy || _project?.Id != projectId) return;
                if (_store.HasChangedSinceLoad())
                {
                    var latest = _store.Load();
                    if (_store.CanSave)
                    {
                        _data = latest;
                        RebuildRows(_selectedNote?.Id, _selectedQueue?.Id);
                    }
                    else Feedback = _store.LoadWarning ?? "Project tasks could not be refreshed.";
                }
            }
            var previousOwnerMessage = _ownerMessage;
            var reply = await QueueOwnerClient.SendAsync(_store.StorePath, "status", projectId,
                cancellationToken: _lifetime.Token);
            if (_disposed || _project?.Id != projectId) return;
            _ownerMessage = reply.Success || reply.Message.StartsWith("An older queue owner", StringComparison.Ordinal)
                ? reply.Message : "";
            _ownerUnavailable = !reply.Success;
            _ownerCanStopCurrent = reply.Success && reply.CanStopCurrent;
            _ownerActiveProjectId = reply.ActiveProjectId;
            _ownerActiveAttemptId = reply.ActiveAttemptId;
            if (reply.Success && previousOwnerMessage.Length > 0 && Feedback == previousOwnerMessage &&
                _ownerMessage != previousOwnerMessage)
                Feedback = _ownerMessage;
            RefreshBindings();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            if (!_disposed)
            {
                _ownerMessage = ex is IOException && ex.Message.StartsWith("An older queue owner", StringComparison.Ordinal)
                    ? ex.Message : "";
                _ownerUnavailable = true;
                RefreshBindings();
            }
        }
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
            queue.ExternalPredecessorSatisfiedAt = null;
        }, "Queue folder updated explicitly. Queue is paused and any external predecessor selection is cleared.");
    }

    private void ClearPredecessor()
    {
        if (!CanClearLegacyPredecessor) return;
        Commit(data =>
        {
            var queue = data.Queues.Single(x => x.ProjectId == _project!.Id);
            queue.ExternalPredecessor = null;
            queue.ExternalPredecessorSatisfiedAt = null;
            queue.StatusMessage = "Old external wait cleared. Queue remains paused.";
        }, "Old external wait cleared. Queue remains paused; use Enable Queue separately.");
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

    private void UseCodexDefault()
    {
        var model = Models.FirstOrDefault(x => x.IsDefault);
        if (model != null) SaveQueueDefaults(model.Id, model.DefaultReasoningEffort);
    }

    private void SaveQueueDefaults(string model, string effort)
    {
        if (_project == null || !Models.Any(x => x.Id == model && x.SupportedReasoningEfforts.Any(e => e.Id == effort))) return;
        var projectId = _project.Id;
        var folder = _folder;
        const string savedMessage = "Codex model and thinking level saved for this queue. Future dispatch uses this selection, including existing pending items.";
        // The queue owner saves receipts independently. Refresh the optimistic
        // baseline before editing, then retry if it writes between load and save.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var latest = _store.Load();
            if (!_store.CanSave)
            {
                Feedback = _store.LoadWarning ?? "Project tasks could not be loaded. Thinking level was not saved.";
                RefreshBindings();
                return;
            }
            _data = latest;
            var candidate = ProjectTaskStore.Clone(latest);
            var queue = candidate.Queues.FirstOrDefault(x => x.ProjectId == projectId);
            if (queue == null)
            {
                queue = new ProjectQueueConfiguration { ProjectId = projectId, AssignedFolder = folder };
                candidate.Queues.Add(queue);
            }
            queue.DefaultModelId = model;
            queue.DefaultReasoningEffort = effort;
            try
            {
                _store.Save(candidate);
                _data = candidate;
                RebuildRows(_selectedNote?.Id, _selectedQueue?.Id);
                Feedback = savedMessage;
                return;
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith(
                "Project tasks changed in another launcher or editor.", StringComparison.Ordinal) && attempt < 2)
            {
                // Retry with the owner's newest receipt and queue state.
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Feedback = ex is ArgumentException or InvalidOperationException ? ex.Message :
                    "Thinking level was not saved. Check access to the task store, then try again.";
                RebuildRows(_selectedNote?.Id, _selectedQueue?.Id);
                return;
            }
        }
    }

    public Task LoadModelsAsync() => RefreshModelsAsync();

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
            var queue = _data.Queues.FirstOrDefault(x => x.ProjectId == _project?.Id);
            _selectedDefaultModel = Models.FirstOrDefault(x => x.Id == queue?.DefaultModelId);
            SyncEfforts(DefaultEfforts, _selectedDefaultModel?.SupportedReasoningEfforts);
            _selectedDefaultEffort = DefaultEfforts.FirstOrDefault(x => x.Id == queue?.DefaultReasoningEffort);
            _selectedModel = Models.FirstOrDefault(x => x.Id == _selectedQueue?.ModelId);
            SyncEfforts(Efforts, _selectedModel?.SupportedReasoningEfforts);
            _selectedEffort = Efforts.FirstOrDefault(x => x.Id == _selectedQueue?.ReasoningEffort);
        }
        finally { _refreshingRows = false; }
    }

    private static void SyncEfforts(ObservableCollection<CodexReasoningEffortOption> displayed,
        IReadOnlyList<CodexReasoningEffortOption>? available)
    {
        available ??= [];
        // Clearing an unchanged ComboBox ItemsSource inside its SelectedItem
        // setter drops the visible selection even though the store saved it.
        if (displayed.SequenceEqual(available)) return;
        displayed.Clear();
        foreach (var option in available) displayed.Add(option);
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
        var receiptId = _selectedReceipt?.Receipt.AttemptId;
        _refreshingRows = true;
        try
        {
            Notes.Clear(); Queue.Clear(); Receipts.Clear();
            if (_project != null)
            {
                foreach (var note in _data.Notes.Where(x => x.ProjectId == _project.Id && !x.IsArchived).OrderBy(x => x.Order).ThenBy(x => x.CreatedAt))
                    Notes.Add(new(note.Id, note.ProjectId, note.Name, note.Prompt, note.IsCompleted, _data.QueueItems.Any(x => x.NoteId == note.Id), false, note.Images.Count));
                foreach (var draft in _drafts.Values.Where(x => x.ProjectId == _project.Id && x.NoteId != null && !_data.Notes.Any(n => n.Id == x.NoteId)))
                    Notes.Add(new(draft.NoteId!, draft.ProjectId, string.IsNullOrWhiteSpace(draft.Name) ? "Recovered note draft" : draft.Name, draft.Prompt, false, false, true, draft.Images.Count));
                foreach (var item in _data.QueueItems.Where(x => x.ProjectId == _project.Id).OrderBy(x => x.Order))
                {
                    var note = _data.Notes.FirstOrDefault(x => x.Id == item.NoteId);
                    Queue.Add(new(item.Id, item.NoteId, note?.Name ?? "Note unavailable", Queue.Count + 1, item.Enabled, item.ModelId, item.ReasoningEffort, item.State));
                }
                foreach (var receipt in _data.Receipts.Where(x =>
                    x.Purpose == ProjectTaskExecutionPurpose.QueueItem && x.Snapshot.ProjectId == _project.Id)
                    .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.AttemptId, StringComparer.Ordinal))
                    Receipts.Add(new(receipt));
            }
            _selectedNote = Notes.FirstOrDefault(x => x.Id == noteId);
            _selectedQueue = Queue.FirstOrDefault(x => x.Id == queueId);
            _selectedReceipt = Receipts.FirstOrDefault(x => x.Receipt.AttemptId == receiptId) ?? Receipts.FirstOrDefault();
        }
        finally { _refreshingRows = false; }
        LoadEditor();
        LoadQueueOptions();
        RefreshBindings();
    }

    private void RefreshBindings()
    {
        RefreshTaskActivity();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshTaskActivity()
    {
        if (_project == null)
        {
            ActivitySummary = "Select a project to see task progress.";
            if (RecentTaskActivity.Count > 0) RecentTaskActivity.Clear();
            if (TaskHistory.Count > 0) TaskHistory.Clear();
            return;
        }

        var allReceipts = _data.Receipts.Where(receipt =>
                receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                receipt.Snapshot.ProjectId == _project.Id)
            .OrderByDescending(receipt => receipt.CreatedAt)
            .ThenByDescending(receipt => receipt.AttemptId, StringComparer.Ordinal)
            .ToList();
        // Retained receipts include retries and removed queue items. Only the
        // latest attempt for each item contributes to current status counts.
        var latest = allReceipts
            .GroupBy(receipt => string.IsNullOrWhiteSpace(receipt.Snapshot.QueueItemId)
                ? receipt.AttemptId : receipt.Snapshot.QueueItemId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var latestIds = latest.Select(receipt => receipt.AttemptId).ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var historyRows = allReceipts.Where(receipt => IsArchivedForDisplay(receipt, latestIds, _data.QueueItems, now))
            .Select(receipt => ActivityRowFor(receipt, inHistory: true)).ToArray();
        if (!TaskHistory.SequenceEqual(historyRows))
        {
            TaskHistory.Clear();
            foreach (var row in historyRows) TaskHistory.Add(row);
        }

        var queue = _data.Queues.FirstOrDefault(candidate => candidate.ProjectId == _project.Id);
        var pending = _data.QueueItems.Where(item =>
            item.ProjectId == _project.Id && item.Enabled && item.State == ProjectQueueItemState.Pending &&
            !latest.Any(receipt => receipt.Snapshot.QueueItemId == item.Id &&
                receipt.QueueReviewCompletedAt == null && receipt.QueueAbandonedAt == null &&
                !IsConfirmedSuccessfulReceipt(receipt))).ToList();

        // A reviewed retry returns its item to Pending. Show the new wait instead
        // of the previous attempt's reviewed failure in the compact strip.
        var pendingIds = pending.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var displayedReceipts = latest.Where(receipt =>
            receipt.ActivityDeletedAt is null && !IsArchivedForDisplay(receipt, latestIds, _data.QueueItems, now) &&
            (!pendingIds.Contains(receipt.Snapshot.QueueItemId) || receipt.QueueReviewCompletedAt == null)).ToList();

        var active = displayedReceipts.Where(IsNonterminalReceipt).ToList();
        var confirmedActive = active.Count(receipt => IsOwnerConfirmedActive(receipt));
        var unconfirmedActive = active.Count - confirmedActive;
        var completed = displayedReceipts.Count(IsConfirmedSuccessfulReceipt);
        var attention = displayedReceipts.Count(receipt => !IsNonterminalReceipt(receipt) &&
            !IsConfirmedSuccessfulReceipt(receipt) && receipt.QueueReviewCompletedAt == null &&
            receipt.QueueAbandonedAt == null);
        var reviewed = displayedReceipts.Count(receipt => receipt.QueueReviewCompletedAt is not null);
        var parts = new List<string>();
        if (confirmedActive > 0) parts.Add($"{confirmedActive} active");
        if (unconfirmedActive > 0) parts.Add($"{unconfirmedActive} status unconfirmed");
        if (completed > 0) parts.Add($"{completed} done");
        if (attention > 0) parts.Add($"{attention} needs review");
        if (reviewed > 0) parts.Add($"{reviewed} reviewed");
        if (pending.Count > 0) parts.Add($"{pending.Count} waiting");
        ActivitySummary = parts.Count == 0 ? "No current tasks." : string.Join(" · ", parts);

        var rows = displayedReceipts.Select(receipt =>
            (Row: ActivityRowFor(receipt), Rank: ActivityRank(receipt), UpdatedAt: receipt.UpdatedAt))
            .Concat(pending.Select(item =>
            {
                var noteName = _data.Notes.FirstOrDefault(note => note.Id == item.NoteId)?.Name ?? "Note unavailable";
                var detail = _data.PauseAllQueues ? "All queues paused" : queue?.Enabled == true
                    ? "Queue enabled; not submitted yet" : "Queue paused; not submitted yet";
                return (Row: new TaskActivityRow(noteName, "Waiting to submit", detail, Brushes.LightGray, null),
                    Rank: 4, UpdatedAt: DateTimeOffset.MinValue);
            }))
            .OrderBy(entry => entry.Rank).ThenByDescending(entry => entry.UpdatedAt)
            .Take(4).Select(entry => entry.Row).ToArray();

        if (!RecentTaskActivity.SequenceEqual(rows))
        {
            RecentTaskActivity.Clear();
            foreach (var row in rows) RecentTaskActivity.Add(row);
        }
    }

    private static HashSet<string> LatestReceiptIds(ProjectTaskData data, string projectId) =>
        data.Receipts.Where(receipt => receipt.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                receipt.Snapshot.ProjectId == projectId)
            .GroupBy(receipt => string.IsNullOrWhiteSpace(receipt.Snapshot.QueueItemId)
                ? receipt.AttemptId : receipt.Snapshot.QueueItemId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(receipt => receipt.CreatedAt)
                .ThenByDescending(receipt => receipt.AttemptId, StringComparer.Ordinal).First().AttemptId)
            .ToHashSet(StringComparer.Ordinal);

    private static bool CanArchiveReceipt(ProjectTaskExecutionReceipt receipt) =>
        IsConfirmedSuccessfulReceipt(receipt) || receipt.QueueReviewCompletedAt is not null;

    private static bool IsArchivedForDisplay(ProjectTaskExecutionReceipt receipt,
        IReadOnlySet<string> latestIds, IReadOnlyCollection<ProjectQueueItem> queueItems, DateTimeOffset now) =>
        receipt.ActivityDeletedAt is null &&
        (receipt.ActivityArchivedAt is not null ||
         IsConfirmedSuccessfulReceipt(receipt) && receipt.FinishedAt <= now.AddMinutes(-10) ||
         receipt.QueueReviewCompletedAt is not null &&
         (!latestIds.Contains(receipt.AttemptId) ||
          !queueItems.Any(item => item.Id == receipt.Snapshot.QueueItemId &&
              item.LastAttemptId == receipt.AttemptId)));

    private static bool IsNonterminalReceipt(ProjectTaskExecutionReceipt receipt) =>
        receipt.QueueReviewCompletedAt == null && receipt.QueueAbandonedAt == null && receipt.State is
            ProjectTaskRunState.Prepared or ProjectTaskRunState.Starting or
            ProjectTaskRunState.Running or ProjectTaskRunState.Recovering;

    private bool IsOwnerConfirmedActive(ProjectTaskExecutionReceipt receipt) =>
        !_ownerUnavailable && _ownerActiveProjectId == _project?.Id &&
        _ownerActiveAttemptId == receipt.AttemptId;

    private int ActivityRank(ProjectTaskExecutionReceipt receipt) =>
        IsNonterminalReceipt(receipt) ? 0 :
        !IsConfirmedSuccessfulReceipt(receipt) && receipt.QueueReviewCompletedAt == null ? 1 :
        IsConfirmedSuccessfulReceipt(receipt) ? 3 : 5;

    private TaskActivityRow ActivityRowFor(ProjectTaskExecutionReceipt receipt, bool inHistory = false)
    {
        var name = string.IsNullOrWhiteSpace(receipt.Snapshot.Name) ? "Untitled queue task" : receipt.Snapshot.Name;
        TaskActivityRow row;
        if (IsConfirmedSuccessfulReceipt(receipt))
            row = new(name, "Done", $"Confirmed finish {receipt.FinishedAt!.Value.ToLocalTime():g}",
                Brushes.LightGreen, receipt.AttemptId);
        else if (IsNonterminalReceipt(receipt))
        {
            var phase = receipt.State switch
            {
                ProjectTaskRunState.Prepared or ProjectTaskRunState.Starting => "starting",
                ProjectTaskRunState.Running => "running",
                _ => "recovering"
            };
            if (!IsOwnerConfirmedActive(receipt))
                row = new(name, "Status unconfirmed", $"Last recorded: {phase} · {receipt.UpdatedAt.ToLocalTime():g}",
                    Brushes.Khaki, receipt.AttemptId);
            else
            {
                var status = phase switch { "starting" => "Starting", "recovering" => "Recovering", _ => "Running" };
                row = new(name, status, $"Owner confirmed · updated {receipt.UpdatedAt.ToLocalTime():g}",
                    Brushes.LightSkyBlue, receipt.AttemptId);
            }
        }
        else if (receipt.QueueReviewCompletedAt != null)
            row = new(name, "Reviewed · not done", $"Outcome retained · reviewed {receipt.QueueReviewCompletedAt.Value.ToLocalTime():g}",
                Brushes.LightGray, receipt.AttemptId);
        else
        {
            var finish = receipt.FinishedAt is { } finished
                ? $"Exact turn finished {finished.ToLocalTime():g}; review required"
                : "No confirmed finish; review required";
            row = receipt.State switch
            {
                ProjectTaskRunState.Failed => new(name, "Failed", finish, Brushes.LightCoral, receipt.AttemptId),
                ProjectTaskRunState.Interrupted => new(name, "Interrupted", finish, Brushes.Khaki, receipt.AttemptId),
                _ => new(name, "Needs review", finish, Brushes.Khaki, receipt.AttemptId)
            };
        }
        return row with
        {
            CanStop = CanStopTask(receipt.AttemptId),
            CanRetry = CanRetryTask(receipt.AttemptId),
            CanMoveToHistory = !inHistory && _store.CanSave && !_queueCommandBusy &&
                receipt.ActivityArchivedAt is null && CanArchiveReceipt(receipt),
            CanDelete = _store.CanSave && !_queueCommandBusy &&
                receipt.ActivityDeletedAt is null && !IsOwnerConfirmedActive(receipt),
            CanReview = !inHistory && !IsConfirmedSuccessfulReceipt(receipt) &&
                receipt.QueueReviewCompletedAt is null && !IsOwnerConfirmedActive(receipt)
        };
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Dispose() { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
    private sealed record NoteDraft(string ProjectId, string? NoteId, string Name, string Prompt, List<ProjectTaskNoteImage> Images);
}

public sealed record NoteRow(string Id, string ProjectId, string Name, string Prompt, bool IsCompleted, bool IsQueued, bool IsDraftOnly = false, int ImageCount = 0)
{
    public string Bullet => IsCompleted ? "✓" : "•";
    public string Detail => (IsDraftOnly ? "Recovered draft · save as a new note" : IsCompleted ? "Complete" : IsQueued ? "In queue" : "Note only") +
        (ImageCount == 0 ? "" : $" · {ImageCount} image{(ImageCount == 1 ? "" : "s")}");
    public string Preview => Prompt.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

public sealed record ProjectApplicationState(string Name, string Kind, string State, string Detail, string? ActiveUrl);

public sealed record QueueReceiptRow(ProjectTaskExecutionReceipt Receipt)
{
    public string Display => $"{Receipt.Snapshot.Name} · {Receipt.State} / {Receipt.Outcome} · {Receipt.CreatedAt.ToLocalTime():g}";
}

public sealed record TaskActivityRow(string Name, string Status, string Detail, Brush StatusBrush, string? AttemptId)
{
    public bool CanStop { get; init; }
    public bool CanRetry { get; init; }
    public bool CanMoveToHistory { get; init; }
    public bool CanDelete { get; init; }
    public bool CanReview { get; init; }
}

public sealed class NoteImageRow : INotifyPropertyChanged
{
    private string _caption;
    private bool _includePageContextInPrompt;
    public NoteImageRow(ProjectTaskNoteImage image)
    {
        Id = image.Id;
        MimeType = image.MimeType;
        DataBase64 = image.DataBase64;
        PageUrl = image.PageUrl;
        PageHtml = image.PageHtml;
        PageCss = image.PageCss;
        PageCaptureStatus = image.PageCaptureStatus;
        _includePageContextInPrompt = image.IncludePageContextInPrompt;
        var bytes = Convert.FromBase64String(DataBase64);
        ByteCount = bytes.Length;
        _caption = image.Caption;
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 320;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            Preview = bitmap;
        }
        catch (Exception)
        {
            Preview = null;
        }
    }
    public string Id { get; }
    public string MimeType { get; }
    public string DataBase64 { get; }
    public string PageUrl { get; }
    public string PageHtml { get; }
    public string PageCss { get; }
    public string PageCaptureStatus { get; }
    public bool HasPageCapture => PageUrl.Length > 0 || PageHtml.Length > 0 || PageCss.Length > 0 || PageCaptureStatus.Length > 0;
    public bool HasPageSource => !string.IsNullOrWhiteSpace(PageHtml) || !string.IsNullOrWhiteSpace(PageCss);
    public string PageSourceSummary => HasPageSource
        ? $"Browser page source saved: {PageHtml.Length:N0} HTML characters, {PageCss.Length:N0} CSS characters. {PageCaptureStatus}".Trim()
        : $"Browser page source unavailable. {PageCaptureStatus}".Trim();
    public bool IncludePageContextInPrompt
    {
        get => _includePageContextInPrompt;
        set
        {
            if (_includePageContextInPrompt == value || !HasPageSource) return;
            _includePageContextInPrompt = value;
            PropertyChanged?.Invoke(this, new(nameof(IncludePageContextInPrompt)));
        }
    }
    public int ByteCount { get; }
    public BitmapSource? Preview { get; }
    public bool PreviewUnavailable => Preview == null;
    public BitmapSource GetFullImage()
    {
        using var stream = new MemoryStream(Convert.FromBase64String(DataBase64));
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
    public string Caption
    {
        get => _caption;
        set { if (_caption == value) return; _caption = value; PropertyChanged?.Invoke(this, new(nameof(Caption))); }
    }
    public ProjectTaskNoteImage ToModel() => new()
    {
        Id = Id, Caption = Caption, MimeType = MimeType, DataBase64 = DataBase64,
        PageUrl = PageUrl, PageHtml = PageHtml, PageCss = PageCss,
        PageCaptureStatus = PageCaptureStatus, IncludePageContextInPrompt = IncludePageContextInPrompt
    };
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record QueueRow(string Id, string NoteId, string Name, int Position, bool Enabled, string ModelId, string ReasoningEffort, ProjectQueueItemState State)
{
    public string Options => string.IsNullOrEmpty(ModelId) ? "Model and thinking level not selected" : $"{ModelId} · {ReasoningEffort}";
    public string Status => !Enabled ? "Disabled" : State == ProjectQueueItemState.Pending ? "Queued · not submitted" : State.ToString();
}

internal sealed class TaskPanelCommand(Action execute, Func<bool> canExecute) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter) { if (canExecute()) execute(); }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
