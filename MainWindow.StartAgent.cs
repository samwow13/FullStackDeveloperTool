using System.IO;
using System.Windows;
using FullStackLauncher.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly Dictionary<string, StartAgentDraft> _startAgentDrafts = new(StringComparer.Ordinal);
    private CodexStartAgentWindow? _startAgentWindow;
    private string? _startAgentResumeOpenProjectId;
    private readonly List<StartAgentResumeEntry> _unmatchedStartAgentResumeEntries = [];
    private bool _startAgentResumePersistenceActive;
    private bool _startAgentResumeUnavailable;

    public bool CanStartAgent => SelectedProject is not null && !_closed && !_closing && !_closeRequested &&
        !_savingProjectEdits && !_releasingWaitingAgents && _startAgentWindow is null;

    private bool HasStartAgentDrafts => _startAgentWindow is not null ||
        _startAgentDrafts.Values.Any(HasUnsentStartAgentDraft);

    private void StartCodexAgent_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartAgent || SelectedProject is not { } project) return;
        OpenStartCodexAgent(project);
    }

    private void OpenStartCodexAgent(ProjectProfile project, bool resumeOpenWindow = false)
    {
        // Capture project identity before the dialog's nested dispatcher can observe other changes.
        var projectRoot = _store.ResolveRoot(project);
        var draft = _startAgentDrafts.GetValueOrDefault(project.Id);
        if (draft is { ProjectRoot: { } savedRoot } && !SameStartAgentRoot(savedRoot, projectRoot))
        {
            if (draft.ResumeState is { } savedState)
                RetainUnmatchedStartAgentResumeEntry(new(project.Id, savedRoot, savedState));
            _startAgentDrafts.Remove(project.Id);
            draft = null;
        }
        if (draft is null && _unmatchedStartAgentResumeEntries.FirstOrDefault(entry =>
                entry.ProjectId == project.Id && SameStartAgentRoot(entry.ProjectRoot, projectRoot)) is { } recovered)
        {
            _unmatchedStartAgentResumeEntries.Remove(recovered);
            draft = DraftFromResumeState(recovered.State, recovered.ProjectRoot);
            _startAgentDrafts[project.Id] = draft;
        }
        CodexStartAgentWindow? window = null;
        try
        {
            window = new CodexStartAgentWindow(project.Id, project.Name, projectRoot,
                draft?.Text ?? "", draft?.PendingAttemptId, draft?.Images, draft?.KeepAwake ?? false,
                inspectSubmission: resumeOpenWindow && draft?.ResumeState?.InspectSubmission == true,
                draftUltraFastEnabled: draft?.UltraFastEnabled, draftFastEnabled: draft?.FastEnabled) { Owner = this };
            _startAgentWindow = window;
            window.ConfigureDashboardResume(draft?.ResumeState, SaveStartAgentDashboardResume);
            window.WaitingSaved += StartAgentWaitingSaved;
            Changed(nameof(CanStartAgent));
            window.ShowDialog();
            if (window.WaitingSavedCount > 0)
                Notice = $"Saved {window.WaitingSavedCount} waiting prompt(s). Choose Go when ready.";
            else if (window.SentSuccessfully)
                Notice = $"New Codex chat started for {project.Name}.";
        }
        catch (Exception ex)
        {
            Notice = $"Start Agent could not open: {SensitiveDataProtection.Redact(ex.Message)}";
            MessageBox.Show(this, Notice, "Start Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (window is not null)
            {
                window.WaitingSaved -= StartAgentWaitingSaved;
                var state = window.CaptureDashboardResumeState();
                if (HasStartAgentResumeState(state))
                    _startAgentDrafts[project.Id] = DraftFromResumeState(state, projectRoot);
                else _startAgentDrafts.Remove(project.Id);
                if (window.WasClosedForDashboardReplacement)
                    _startAgentResumeOpenProjectId = project.Id;
                else if (_startAgentResumeOpenProjectId == project.Id)
                    _startAgentResumeOpenProjectId = null;
            }
            _startAgentWindow = null;
            if (_startAgentResumePersistenceActive)
            {
                try { SaveStartAgentDashboardResume(); }
                catch (Exception)
                {
                    Notice = "Start Agent draft could not be autosaved. Its current draft is kept in Launcher.";
                }
            }
            Changed(nameof(CanStartAgent));
            UpdateWaitingAgentActions();
            _ = RefreshWaitingAgentsAsync();
        }
    }

    private bool PrepareStartAgentDraftsForClose(bool automaticReplacement = false)
    {
        if (_releasingWaitingAgents || _stoppingWaitingAgentQueue)
        {
            Notice = _stoppingWaitingAgentQueue ? "Saving Stop queue. Close Launcher when it finishes."
                : "Waiting prompts are starting. Close Launcher after submission checks finish.";
            return false;
        }
        if (_startAgentWindow is not null) return false;
        PreserveStartAgentDraftsForChangedRoots();
        if (automaticReplacement)
        {
            SaveStartAgentDashboardResume();
            return true;
        }
        var count = _startAgentDrafts.Values.Count(HasUnsentStartAgentDraft);
        if (count == 0) return true;
        var discard = MessageBox.Show(this,
            $"Discard {count} unsent Start Agent message{(count == 1 ? "" : "s")} and close Launcher? " +
            "Saved project context and submitted chats are kept.",
            "Unsent Codex messages", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!discard) return false;
        if (_startAgentResumePersistenceActive)
        {
            if (_unmatchedStartAgentResumeEntries.Count == 0) _dashboardResumeStore.Remove("start-agent");
            else _dashboardResumeStore.Save("start-agent",
                new StartAgentDashboardResume(1, [.. _unmatchedStartAgentResumeEntries], null));
        }
        _startAgentDrafts.Clear();
        _startAgentResumeOpenProjectId = null;
        return true;
    }

    private bool PrepareStartAgentDraftForRemoval(string projectId)
    {
        if (_startAgentWindow is not null) return false;
        if (!_startAgentDrafts.TryGetValue(projectId, out var draft) || !HasUnsentStartAgentDraft(draft)) return true;
        return MessageBox.Show(this,
            "Discard this project's unsent Start Agent message and remove the profile? " +
            "Saved context and submitted chats are kept.",
            "Unsent Codex message", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private sealed record StartAgentDraft(string Text, string? PendingAttemptId, IReadOnlyList<CodexAgentChatImage> Images,
        bool KeepAwake, bool UltraFastEnabled = false, bool FastEnabled = false,
        CodexStartAgentResumeState? ResumeState = null, string? ProjectRoot = null);

    private static StartAgentDraft DraftFromResumeState(CodexStartAgentResumeState state, string? projectRoot = null) =>
        new(state.Text, state.PreviousAttemptId, state.Images, state.KeepAwake,
            state.UltraFastEnabled, state.FastEnabled, state, projectRoot);

    private static bool HasStartAgentResumeState(CodexStartAgentResumeState state) =>
        !string.IsNullOrEmpty(state.Text) || state.Images.Count > 0 || state.PreviousAttemptId is not null ||
        state.ContextDraft is not null || state.Gatherer is not null || state.KeepAwake ||
        state.UltraFastEnabled || state.FastEnabled || state.ModelId is not null || state.AccessMode is not null;

    private static bool HasUnsentStartAgentDraft(StartAgentDraft draft) =>
        !string.IsNullOrWhiteSpace(draft.Text) || draft.Images.Count > 0 || draft.ResumeState?.ContextDraft is not null ||
        draft.ResumeState?.Gatherer is { } gatherer && (gatherer.ConfirmedImages.Count > 0 || gatherer.PendingImage is not null);

    private void PreserveStartAgentDraftsForChangedRoots()
    {
        foreach (var (projectId, draft) in _startAgentDrafts.ToArray())
        {
            if (draft.ProjectRoot is not { } root || draft.ResumeState is not { } state) continue;
            var project = Projects.FirstOrDefault(project => project.Id == projectId);
            if (project is not null && SameStartAgentRoot(root, _store.ResolveRoot(project))) continue;
            RetainUnmatchedStartAgentResumeEntry(new(projectId, root, state));
            _startAgentDrafts.Remove(projectId);
            if (_startAgentResumeOpenProjectId == projectId) _startAgentResumeOpenProjectId = null;
        }
    }

    private bool ForgetStartAgentDashboardResumeProject(string projectId, string projectRoot)
    {
        if (_startAgentDrafts.TryGetValue(projectId, out var draft))
        {
            if (draft.ProjectRoot is { } savedRoot && !SameStartAgentRoot(savedRoot, projectRoot) &&
                draft.ResumeState is { } state)
                RetainUnmatchedStartAgentResumeEntry(new(projectId, savedRoot, state));
            _startAgentDrafts.Remove(projectId);
        }
        _unmatchedStartAgentResumeEntries.RemoveAll(entry => entry.ProjectId == projectId &&
            SameStartAgentRoot(entry.ProjectRoot, projectRoot));
        if (_startAgentResumeOpenProjectId == projectId) _startAgentResumeOpenProjectId = null;
        if (_startAgentResumeUnavailable) return false;
        if (!_startAgentResumePersistenceActive) return true;
        try
        {
            SaveStartAgentDashboardResume();
            return true;
        }
        catch (Exception) { return false; }
    }

    private void RetainUnmatchedStartAgentResumeEntry(StartAgentResumeEntry entry)
    {
        _unmatchedStartAgentResumeEntries.RemoveAll(previous => previous.ProjectId == entry.ProjectId &&
            SameStartAgentRoot(previous.ProjectRoot, entry.ProjectRoot));
        _unmatchedStartAgentResumeEntries.Add(entry);
    }

    private void SaveStartAgentDashboardResume()
    {
        if (_startAgentResumeUnavailable)
            throw new IOException("Start Agent resume data could not be read. Existing saved drafts were preserved.");
        PreserveStartAgentDraftsForChangedRoots();
        var entries = new List<StartAgentResumeEntry>(_unmatchedStartAgentResumeEntries);
        foreach (var (projectId, draft) in _startAgentDrafts)
        {
            var project = Projects.FirstOrDefault(project => project.Id == projectId);
            if (project is null) continue;
            var state = draft.ResumeState ?? new CodexStartAgentResumeState
            {
                Text = draft.Text, PreviousAttemptId = draft.PendingAttemptId, Images = draft.Images,
                KeepAwake = draft.KeepAwake, UltraFastEnabled = draft.UltraFastEnabled,
                FastEnabled = draft.FastEnabled
            };
            var root = draft.ProjectRoot ?? _store.ResolveRoot(project);
            entries.RemoveAll(entry => entry.ProjectId == projectId && SameStartAgentRoot(entry.ProjectRoot, root));
            entries.Add(new(projectId, root, state));
        }
        var openProjectId = _startAgentResumeOpenProjectId;
        if (_startAgentWindow is { } window)
        {
            var project = Projects.FirstOrDefault(project => project.Id == window.ProjectId)
                ?? throw new IOException("Start Agent project changed. Its draft is kept in Launcher.");
            var root = window.ProjectFolder;
            entries.RemoveAll(entry => entry.ProjectId == project.Id && SameStartAgentRoot(entry.ProjectRoot, root));
            entries.Add(new(project.Id, root, window.CaptureDashboardResumeState()));
            openProjectId = project.Id;
        }
        _dashboardResumeStore.Save("start-agent", new StartAgentDashboardResume(1, entries, openProjectId));
        _startAgentResumePersistenceActive = true;
    }

    private void RestoreStartAgentDashboardResume()
    {
        StartAgentDashboardResume? saved;
        try
        {
            saved = _dashboardResumeStore.Load<StartAgentDashboardResume>("start-agent");
            if (saved is null) return;
            if (saved.Version != 1 || saved.Drafts is null || saved.Drafts.Count > 10_000)
                throw new IOException("Saved Start Agent drafts are invalid. Existing saved drafts were preserved.");
            var identities = new HashSet<(string, string)>();
            foreach (var entry in saved.Drafts)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.ProjectId) ||
                    string.IsNullOrWhiteSpace(entry.ProjectRoot) || !Path.IsPathFullyQualified(entry.ProjectRoot) ||
                    entry.State is not { Text: not null, Images: not null } state ||
                    state.AccessMode is { } access && !Enum.IsDefined(access) ||
                    state.PreviousAttemptId is { } attempt && !Guid.TryParseExact(attempt, "N", out _) ||
                    !double.IsFinite(state.PromptScrollOffset))
                    throw new IOException("Saved Start Agent drafts are invalid. Existing saved drafts were preserved.");
                if (!identities.Add((entry.ProjectId, Path.GetFullPath(entry.ProjectRoot).TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant())))
                    throw new IOException("Saved Start Agent drafts contain a repeated identity. Existing saved drafts were preserved.");
                CodexAgentImageStaging.ValidateImages(state.Images);
                if (state.Gatherer is { } gatherer)
                    CodexAgentImageStaging.ValidateImages(state.Images.Concat(gatherer.ConfirmedImages)
                        .Concat(gatherer.PendingImage is { } pending ? [pending] : []).ToArray());
            }
        }
        catch (Exception)
        {
            _startAgentResumeUnavailable = true;
            throw new IOException("Start Agent resume data could not be read. Existing saved drafts were preserved.");
        }
        _startAgentResumePersistenceActive = true;
        foreach (var entry in saved.Drafts)
        {
            var project = Projects.FirstOrDefault(project => project.Id == entry.ProjectId);
            if (project is null || !SameStartAgentRoot(entry.ProjectRoot, _store.ResolveRoot(project)))
            {
                RetainUnmatchedStartAgentResumeEntry(entry);
                continue;
            }
            _startAgentDrafts[entry.ProjectId] = DraftFromResumeState(entry.State, entry.ProjectRoot);
        }
        if (saved.OpenProjectId is { } projectId && _startAgentDrafts.ContainsKey(projectId))
            _startAgentResumeOpenProjectId = projectId;
    }

    private void ResumeStartAgentDashboardWindow()
    {
        if (_startAgentResumeOpenProjectId is not { } projectId || _startAgentWindow is not null ||
            _closed || _closing || _closeRequested || _checkingStartupServices) return;
        var project = Projects.FirstOrDefault(project => project.Id == projectId);
        if (project is null) return;
        // Clear the reopen intent only after a matching composer can take ownership.
        _startAgentResumeOpenProjectId = null;
        OpenStartCodexAgent(project, resumeOpenWindow: true);
    }

    private static bool SameStartAgentRoot(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private sealed record StartAgentResumeEntry(string ProjectId, string ProjectRoot, CodexStartAgentResumeState State);
    private sealed record StartAgentDashboardResume(int Version, List<StartAgentResumeEntry> Drafts, string? OpenProjectId);
}
