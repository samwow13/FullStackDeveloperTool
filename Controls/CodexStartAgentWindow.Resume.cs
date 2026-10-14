using FullStackLauncher.ProjectTasks;

namespace FullStackLauncher.Controls;

public partial class CodexStartAgentWindow
{
    internal void ConfigureDashboardResume(CodexStartAgentResumeState? state, Action saveDashboardResume)
    {
        ArgumentNullException.ThrowIfNull(saveDashboardResume);
        _saveDashboardResume = saveDashboardResume;
        if (state is null) return;
        // Model discovery still decides availability. Restored choices never start a chat.
        _resumeModelId = state.ModelId;
        _resumeReasoningEffort = state.ReasoningEffort;
        _resumeAccessMode = state.AccessMode;
        _dashboardResumeGatherer = state.Gatherer;
        _resumeCaretIndex = state.CaretIndex;
        _resumePromptScrollOffset = state.PromptScrollOffset;
        if (state.ContextDraft is not null)
        {
            ContextBox.Text = state.ContextDraft;
            if (_preferences is not null && state.ContextRevision is not null)
            {
                _resumeContextConflict = !string.Equals(_preferences.Context,
                    state.ContextBaseline ?? _preferences.Context, StringComparison.Ordinal);
                // The file revision also covers other projects. Only a changed target
                // context keeps the old guard; unrelated prior saves use the fresh revision.
                if (_resumeContextConflict) _preferences = _preferences with
                {
                    Context = state.ContextBaseline ?? _preferences.Context,
                    Revision = state.ContextRevision
                };
            }
        }
        ContextExpander.IsExpanded = state.ContextExpanded;
        UpdateControls();
    }

    private void RestoreDashboardResumeCursor()
    {
        if (_resumeCaretIndex is not { } caret) return;
        PromptBox.CaretIndex = Math.Clamp(caret, 0, PromptBox.Text.Length);
        PromptBox.ScrollToVerticalOffset(Math.Max(0, _resumePromptScrollOffset));
        _resumeCaretIndex = null;
    }

    internal CodexStartAgentResumeState CaptureDashboardResumeState()
    {
        var composer = _composerDraft;
        // The visible submitted request belongs to its receipt, never to the new draft.
        var showingSubmission = PendingAttemptId is not null || composer is not null;
        return new()
        {
            Text = Draft,
            Images = UnsentDraftImages.ToArray(),
            KeepAwake = DraftKeepAwake,
            UltraFastEnabled = DraftUltraFastEnabled,
            FastEnabled = DraftFastEnabled,
            PreviousAttemptId = RecoveryAttemptId,
            InspectSubmission = showingSubmission && RecoveryAttemptId is not null,
            ContextDraft = HasUnsavedContext ? ContextBox.Text : null,
            ContextBaseline = HasUnsavedContext ? _preferences?.Context : null,
            ContextRevision = HasUnsavedContext ? _preferences?.Revision : null,
            ModelId = composer?.Model?.Id ?? (!showingSubmission
                ? (ModelChoice.SelectedItem as CodexModelOption)?.Id ?? _resumeModelId : null),
            ReasoningEffort = composer?.Effort?.Id ?? (!showingSubmission
                ? (EffortChoice.SelectedItem as CodexReasoningEffortOption)?.Id ?? _resumeReasoningEffort : null),
            AccessMode = composer?.Access?.Mode ?? (!showingSubmission
                ? (AccessChoice.SelectedItem as AgentAccessOption)?.Mode ?? _resumeAccessMode : null),
            ContextExpanded = ContextExpander.IsExpanded,
            CaretIndex = showingSubmission ? 0 : PromptBox.CaretIndex,
            PromptScrollOffset = showingSubmission ? 0 : PromptBox.VerticalOffset,
            Gatherer = _dashboardResumeGatherer
        };
    }

    internal async Task PrepareDashboardResumeCloseAsync()
    {
        if (_closed || !IsDashboardReplacementReady)
            throw new InvalidOperationException("Start Agent is still finishing an operation. Its draft is kept.");
        if (_saveDashboardResume is null)
            throw new InvalidOperationException("Start Agent resume storage is unavailable. Its draft is kept.");
        _closingDictation = true;
        UpdateControls();
        try
        {
            if (_dictationSession is not null && !await StopDictationAsync())
                throw new InvalidOperationException("Dictation could not finish. Review the recording message before restarting Launcher. Your draft is kept.");
            _saveDashboardResume();
            // Microphone capture never resumes automatically on the replacement dashboard.
            // Skip normal context/discard prompts only after the complete editable draft is durable.
            _dashboardResumeClose = true;
        }
        finally
        {
            _closingDictation = false;
            UpdateControls();
        }
    }
}
