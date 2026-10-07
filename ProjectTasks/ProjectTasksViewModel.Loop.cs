using System.IO;
using System.Windows.Input;

namespace FullStackLauncher.ProjectTasks;

public sealed partial class ProjectTasksViewModel
{
    private readonly Dictionary<string, AutomaticLoopDraft> _automaticLoopDrafts = new(StringComparer.Ordinal);
    private ICommand? _saveAutomaticLoopSettingsCommand;
    private ProjectQueueConfiguration? AutomaticLoopQueue => _data.Queues.FirstOrDefault(x => x.ProjectId == _project?.Id);
    private AutomaticLoopDraft SavedAutomaticLoopSettings => new(
        string.IsNullOrWhiteSpace(AutomaticLoopQueue?.AutomaticLoopFolder) ? AssignedFolder : AutomaticLoopQueue.AutomaticLoopFolder,
        AutomaticLoopQueue?.AutomaticLoopModelId ?? "gpt-6-luna",
        AutomaticLoopQueue?.AutomaticLoopReasoningEffort ?? "high",
        AutomaticLoopQueue?.AutomaticLoopAppGoal ?? "");
    private AutomaticLoopDraft AutomaticLoopSettings => _project != null && _automaticLoopDrafts.TryGetValue(_project.Id, out var draft)
        ? draft : SavedAutomaticLoopSettings;

    public bool CanEditAutomaticLoopSettings => CanEdit && !_queueCommandBusy && !HasOutstandingRun;
    public bool CanToggleAutomaticLoop => CanEdit && !_queueCommandBusy && (AutomaticLoopEnabled || !HasOutstandingRun);
    public bool CanChooseAutomaticLoopModel => CanEditAutomaticLoopSettings && Models.Count > 0 && !_loadingModels;
    public bool AutomaticLoopEnabled
    {
        get => AutomaticLoopQueue?.AutomaticLoopEnabled == true;
        set
        {
            if (value == AutomaticLoopEnabled || !CanToggleAutomaticLoop) return;
            // Enabling uses saved settings. Draft settings require their explicit Save.
            var settings = SavedAutomaticLoopSettings;
            SaveAutomaticLoopConfiguration(value, settings,
                value ? "Automatic Loop Mode enabled. Your next successful manual queue item starts the loop. Enable Queue separately."
                    : "Automatic Loop Mode disabled. Pending loop items are disabled; any active task continues.");
            RefreshBindings();
        }
    }

    public string AutomaticLoopFolderText
    {
        get => AutomaticLoopSettings.Folder;
        set => UpdateAutomaticLoopDraft(AutomaticLoopSettings with { Folder = value });
    }
    public string AutomaticLoopAppGoalText
    {
        get => AutomaticLoopSettings.AppGoal;
        set => UpdateAutomaticLoopDraft(AutomaticLoopSettings with { AppGoal = value });
    }
    public CodexModelOption? SelectedAutomaticLoopModel
    {
        get => Models.FirstOrDefault(x => x.Id == AutomaticLoopSettings.ModelId);
        set
        {
            if (_refreshingRows || value == null || !CanChooseAutomaticLoopModel || value.Id == AutomaticLoopSettings.ModelId) return;
            var effort = value.SupportedReasoningEfforts.FirstOrDefault(x => x.Id == AutomaticLoopSettings.Effort)
                ?? value.SupportedReasoningEfforts.FirstOrDefault(x => x.Id == value.DefaultReasoningEffort)
                ?? value.SupportedReasoningEfforts.FirstOrDefault();
            if (effort != null) UpdateAutomaticLoopDraft(AutomaticLoopSettings with { ModelId = value.Id, Effort = effort.Id });
        }
    }
    public IReadOnlyList<CodexReasoningEffortOption> AutomaticLoopEfforts => SelectedAutomaticLoopModel?.SupportedReasoningEfforts ?? [];
    public CodexReasoningEffortOption? SelectedAutomaticLoopEffort
    {
        get => AutomaticLoopEfforts.FirstOrDefault(x => x.Id == AutomaticLoopSettings.Effort);
        set
        {
            if (_refreshingRows || value == null || !CanChooseAutomaticLoopModel || value.Id == AutomaticLoopSettings.Effort) return;
            UpdateAutomaticLoopDraft(AutomaticLoopSettings with { Effort = value.Id });
        }
    }
    public ICommand SaveAutomaticLoopSettingsCommand => _saveAutomaticLoopSettingsCommand ??= new TaskPanelCommand(
        SaveAutomaticLoopSettings, () => CanEditAutomaticLoopSettings && _project != null && _automaticLoopDrafts.ContainsKey(_project.Id));
    public string AutomaticLoopStatus
    {
        get
        {
            if (_project == null) return "Select a project to configure loop mode.";
            if (_automaticLoopDrafts.ContainsKey(_project.Id)) return "Unsaved loop settings. Select Save loop settings to apply.";
            var queue = AutomaticLoopQueue;
            if (queue?.AutomaticLoopEnabled != true)
                return $"Off · {SavedAutomaticLoopSettings.ModelId} / {SavedAutomaticLoopSettings.Effort}.";
            if (queue.DelayBetweenTasksMinutes < 1) return "Set and save a delay of at least 1 minute before loop tasks can run.";
            if (Models.Count > 0 && !Models.Any(x => x.Id == queue.AutomaticLoopModelId &&
                x.SupportedReasoningEfforts.Any(e => e.Id == queue.AutomaticLoopReasoningEffort)))
                return "Saved loop model or thinking level is unavailable. Choose another selection.";
            return string.IsNullOrWhiteSpace(queue.AutomaticLoopSeedAttemptId)
                ? "On · Waiting for your first successful manual queue item."
                : $"On · {queue.AutomaticLoopModelId} / {queue.AutomaticLoopReasoningEffort} · {queue.DelayBetweenTasksMinutes} minute delay.";
        }
    }

    private void UpdateAutomaticLoopDraft(AutomaticLoopDraft draft)
    {
        if (_refreshingRows || _project == null || !CanEditAutomaticLoopSettings) return;
        if (draft == SavedAutomaticLoopSettings) _automaticLoopDrafts.Remove(_project.Id);
        else _automaticLoopDrafts[_project.Id] = draft;
        Changed(nameof(AutomaticLoopFolderText));
        Changed(nameof(AutomaticLoopAppGoalText));
        Changed(nameof(SelectedAutomaticLoopModel));
        Changed(nameof(AutomaticLoopEfforts));
        Changed(nameof(SelectedAutomaticLoopEffort));
        Changed(nameof(AutomaticLoopStatus));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SaveAutomaticLoopSettings()
    {
        if (!CanEditAutomaticLoopSettings || _project == null) return;
        var projectId = _project.Id;
        if (SaveAutomaticLoopConfiguration(AutomaticLoopEnabled, AutomaticLoopSettings,
            "Loop settings saved. Folder or app-goal changes wait for a new successful manual queue item."))
        {
            _automaticLoopDrafts.Remove(projectId);
            RefreshBindings();
        }
    }

    private bool SaveAutomaticLoopConfiguration(bool enabled, AutomaticLoopDraft settings, string message)
    {
        if (_project == null) return false;
        var projectId = _project.Id;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var latest = LoadTaskData();
                if (!_store.CanSave) throw new InvalidOperationException(_store.LoadWarning ?? "Loop settings could not be saved.");
                _data = latest;
                var candidate = ProjectTaskStore.Clone(latest);
                var queue = candidate.Queues.FirstOrDefault(x => x.ProjectId == projectId);
                if (queue == null)
                {
                    queue = new ProjectQueueConfiguration { ProjectId = projectId, AssignedFolder = _folder };
                    candidate.Queues.Add(queue);
                }
                if (enabled)
                {
                    if (candidate.Receipts.Any(x => x.Purpose == ProjectTaskExecutionPurpose.QueueItem &&
                        x.Snapshot.ProjectId == projectId && x.QueueReviewCompletedAt == null && x.QueueAbandonedAt == null &&
                        !IsConfirmedSuccessfulReceipt(x)))
                        throw new InvalidOperationException("Wait for the current attempt to finish or review its hold before changing loop settings.");
                    if (!Models.Any(x => x.Id == settings.ModelId && x.SupportedReasoningEfforts.Any(e => e.Id == settings.Effort)))
                        throw new InvalidOperationException("Refresh models and choose an available loop model and thinking level.");
                    var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.Folder.Trim()));
                    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_folder));
                    if (!folder.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                        !folder.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Choose this project's root folder or a folder inside it.");
                }
                ProjectAutomaticLoop.Configure(candidate, queue, enabled, settings.Folder, settings.ModelId, settings.Effort,
                    DateTimeOffset.UtcNow, settings.AppGoal);
                _store.Save(candidate);
                _data = candidate;
                RebuildRows(_selectedNote?.Id, _selectedQueue?.Id);
                Feedback = message;
                return true;
            }
            catch (ProjectTaskStoreConflictException) when (attempt < 2) { }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Feedback = ex is ArgumentException or InvalidOperationException ? ex.Message
                    : "Loop settings were not saved. Check access to the task store, then retry.";
                RefreshBindings();
                return false;
            }
        }
        return false;
    }

    private sealed record AutomaticLoopDraft(string Folder, string ModelId, string Effort, string AppGoal);
}
