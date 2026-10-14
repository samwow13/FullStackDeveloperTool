using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

/// <summary>A project-scoped prompt draft and explicit, independent Codex chat submission.</summary>
public partial class CodexStartAgentWindow : Window, IDashboardReplacementWindow
{
    private bool _replacementClosePending;
    private bool _dashboardResumeClose;
    private Action? _saveDashboardResume;
    private string? _resumeModelId;
    private string? _resumeReasoningEffort;
    private CodexAgentAccessMode? _resumeAccessMode;
    private bool _resumeContextConflict;
    private int? _resumeCaretIndex;
    private double _resumePromptScrollOffset;
    public bool IsDashboardReplacementReady => !IsSubmitting && !_savingContext && !_checkingStatus &&
        !_closingContext && !_pastingImage && !_loadingAccess && !_changingContextGatherer && !_receivingContextGathererDraft &&
        !_changingDictation && !_closingDictation;
    public bool IsDashboardReplacementClosePending => _replacementClosePending;
    internal string ProjectId => _projectId;
    internal string ProjectFolder => _folder;
    internal bool WasClosedForDashboardReplacement => _dashboardResumeClose;

    private readonly string _projectId;
    private readonly string _projectName;
    private readonly string _folder;
    private readonly CodexAgentPromptStore _store = new();
    private readonly CodexAgentChatClient _client = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CodexAgentPromptPreferences? _preferences;
    private bool _initializing = true;
    private bool _loadingModels;
    private bool _checkingStatus;
    private bool _savingContext;
    private bool _closed;
    private bool _discardContext;
    private bool _preferencesAvailable;
    private bool _restoringModels;
    private bool _closingContext;
    private bool _recoveryAvailable = true;
    private bool _showingFrozenModel;
    private bool _loadingAccess;
    private bool _accessAvailable;
    private bool _showingFrozenAccess;
    private bool _updatingSpeedChoices;
    private CodexAgentChatReceipt? _pendingReceipt;
    private string? _previousAttemptId;
    private ComposerDraft? _composerDraft;

    private bool CanChooseAgentSettings => !IsSubmitting && !_checkingStatus && !_savingContext && !_pastingImage &&
        !_changingContextGatherer && !_receivingContextGathererDraft && PendingAttemptId is null &&
        _composerDraft is null && !_loadingModels && !_loadingAccess && !IsDictating;

    // Submitted messages are immutable receipts, not unsent drafts for the next chat.
    public string Draft => _composerDraft?.Text ?? (PendingAttemptId is null ? PromptBox.Text : "");
    public bool DraftKeepAwake => _composerDraft?.KeepAwake ??
        (PendingAttemptId is null && KeepAwakeChoice.IsChecked == true);
    public bool DraftUltraFastEnabled => _composerDraft?.UltraFastEnabled ??
        (PendingAttemptId is null && UltraFastChoice.IsChecked == true);
    public bool DraftFastEnabled => _composerDraft?.FastEnabled ??
        (PendingAttemptId is null && FastChoice.IsChecked == true);
    public IReadOnlyList<CodexAgentChatImage> UnsentDraftImages => _composerDraft?.Images ??
        (PendingAttemptId is null ? DraftImages : []);
    public string? RecoveryAttemptId => PendingAttemptId ?? _previousAttemptId;
    public bool HasUnsavedContext => !_discardContext && _preferences is not null &&
        !string.Equals(ContextBox.Text, _preferences.Context, StringComparison.Ordinal);
    public bool IsSubmitting { get; private set; }
    public string? PendingAttemptId { get; private set; }
    public bool SentSuccessfully { get; private set; }
    public string? ThreadId { get; private set; }

    public CodexStartAgentWindow(string projectId, string projectName, string folder, string draft = "",
        string? pendingAttemptId = null, IReadOnlyList<CodexAgentChatImage>? draftImages = null,
        bool keepAwake = false, bool inspectSubmission = false,
        bool? draftUltraFastEnabled = null, bool? draftFastEnabled = null)
    {
        FullStackLauncher.Services.WindowTheme.Attach(this);
        _projectId = projectId;
        _projectName = projectName;
        _folder = folder;
        _previousAttemptId = pendingAttemptId;
        InitializeComponent();
        InitializeContentSizing();
        LocationChanged += (_, _) => SettingsPopup.IsOpen = false;
        SizeChanged += (_, _) => SettingsPopup.IsOpen = false;
        Deactivated += (_, _) => SettingsPopup.IsOpen = false;
        ProjectTitle.Text = projectName;
        ProjectTitle.ToolTip = folder;
        PromptBox.Text = draft;
        KeepAwakeChoice.IsChecked = keepAwake;
        InitializeMessageImages(draftImages);
        AccessChoice.ItemsSource = new[]
        {
            new AgentAccessOption(CodexAgentAccessMode.Workspace, "Workspace"),
            new AgentAccessOption(CodexAgentAccessMode.FullAccess, "Full access")
        };
        AccessChoice.DisplayMemberPath = nameof(AgentAccessOption.Label);
        try
        {
            _preferences = _store.Load(projectId);
            ContextBox.Text = _preferences.Context;
            _preferencesAvailable = true;
        }
        catch (Exception ex)
        {
            ShowStatus("Saved agent preferences could not be loaded. Your message is kept. " +
                SensitiveDataProtection.Redact(ex.Message), error: true);
        }
        UltraFastChoice.IsChecked = draftUltraFastEnabled ?? _preferences?.UltraFastEnabled ?? false;
        FastChoice.IsChecked = draftFastEnabled ?? _preferences?.FastEnabled ?? false;
        _initializing = false;
        UpdateControls();
        Loaded += async (_, _) =>
        {
            PromptBox.Focus();
            PromptBox.CaretIndex = PromptBox.Text.Length;
            RestoreDashboardResumeCursor();
            // Ordinary Start Agent opens a composer. Explicit submission review uses
            // the same recovery path, preserving the unsent composer separately.
            if (!_closed && _preferencesAvailable)
            {
                await LoadAccessAsync();
                if (_accessAvailable) await LoadModelsAsync();
            }
            if (!_closed && inspectSubmission && pendingAttemptId is not null)
                await InspectSubmissionAsync(pendingAttemptId);
            if (!_closed && _resumeContextConflict)
                ShowStatus("Saved project context changed while Launcher restarted. Your restored text is kept; reopen the composer before saving project context.", error: true);
            if (!_closed && _dashboardResumeGatherer is not null && PendingAttemptId is null && _composerDraft is null)
                await OpenContextGathererAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            ReleaseDictationOnClose();
            SettingsPopup.IsOpen = false;
            _lifetime.Cancel();
            _ = StopContextGathererAsync();
            ForgetLastCaptureSource();
            _lifetime.Dispose();
        };
    }

    private async Task LoadModelsAsync()
    {
        if (_loadingModels || IsSubmitting || PendingAttemptId is not null || _composerDraft is not null) return;
        _loadingModels = true;
        var wantedModel = (ModelChoice.SelectedItem as CodexModelOption)?.Id ?? _resumeModelId ?? _preferences?.ModelId ?? "";
        var wantedEffort = (EffortChoice.SelectedItem as CodexReasoningEffortOption)?.Id ?? _resumeReasoningEffort ?? _preferences?.ReasoningEffort ?? "";
        ShowStatus("Loading Codex models…");
        UpdateControls();
        try
        {
            var models = await new CodexModelCatalog().LoadAsync(_folder, _lifetime.Token);
            if (_closed) return;
            _restoringModels = true;
            try
            {
                ModelChoice.ItemsSource = models;
                var selected = models.FirstOrDefault(model => model.Id == wantedModel);
                if (selected is null && string.IsNullOrEmpty(wantedModel))
                    selected = models.FirstOrDefault(model => model.IsDefault) ?? models.FirstOrDefault();
                ModelChoice.SelectedItem = selected;
                SetEfforts(selected, wantedEffort);
                if (selected is not null)
                {
                    _resumeModelId = null;
                    _resumeReasoningEffort = null;
                }
                if (selected is null)
                    ShowStatus("Saved model is unavailable. Choose an available model.");
                else
                    ClearStatus();
            }
            finally { _restoringModels = false; }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (!_closed) ShowStatus(SensitiveDataProtection.Redact(ex.Message), error: true);
        }
        finally
        {
            _loadingModels = false;
            if (!_closed) UpdateControls();
        }
    }

    private void SetEfforts(CodexModelOption? model, string wanted)
    {
        EffortChoice.ItemsSource = model?.SupportedReasoningEfforts;
        EffortChoice.SelectedItem = model?.SupportedReasoningEfforts.FirstOrDefault(effort => effort.Id == wanted)
            ?? model?.SupportedReasoningEfforts.FirstOrDefault(effort => effort.Id == model.DefaultReasoningEffort);
    }

    private void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _restoringModels) return;
        var selected = ModelChoice.SelectedItem as CodexModelOption;
        SetEfforts(selected, (EffortChoice.SelectedItem as CodexReasoningEffortOption)?.Id ?? "");
        UpdateControls();
    }

    private void Effort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing) UpdateControls();
    }

    private void Speed_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _updatingSpeedChoices) return;
        _updatingSpeedChoices = true;
        try
        {
            if (ReferenceEquals(sender, UltraFastChoice) && UltraFastChoice.IsChecked == true)
                FastChoice.IsChecked = false;
            else if (ReferenceEquals(sender, FastChoice) && FastChoice.IsChecked == true)
                UltraFastChoice.IsChecked = false;
        }
        finally { _updatingSpeedChoices = false; }
        UpdateControls();
    }

    private sealed record AgentAccessOption(CodexAgentAccessMode Mode, string Label);

    private async Task LoadAccessAsync()
    {
        if (_loadingAccess || IsSubmitting || PendingAttemptId is not null || _composerDraft is not null || _accessAvailable) return;
        _loadingAccess = true;
        UpdateControls();
        try
        {
            var mode = _resumeAccessMode ?? _preferences?.AccessMode ??
                await CodexAgentAccessPolicy.LoadConfiguredDefaultAsync(_folder, _lifetime.Token);
            if (_closed) return;
            AccessChoice.SelectedItem = AccessChoice.Items.OfType<AgentAccessOption>().First(option => option.Mode == mode);
            _accessAvailable = true;
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (!_closed) ShowStatus("Agent access could not be loaded. Use Agent settings > Retry loading choices. " +
                SensitiveDataProtection.Redact(ex.Message), error: true);
        }
        finally
        {
            _loadingAccess = false;
            if (!_closed) UpdateControls();
        }
    }

    private void Access_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _restoringModels) return;
        _accessAvailable = AccessChoice.SelectedItem is AgentAccessOption;
        UpdateControls();
    }

    private void Prompt_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_initializing) UpdateControls();
    }

    private void Context_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        _discardContext = false;
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (_initializing) return;
        var locked = IsSubmitting || _checkingStatus || _savingContext || _pastingImage ||
            _changingContextGatherer || _receivingContextGathererDraft || _closingDictation ||
            PendingAttemptId is not null || _composerDraft is not null;
        var canChoose = CanChooseAgentSettings;
        ModelChoice.IsEnabled = canChoose;
        EffortChoice.IsEnabled = canChoose;
        var model = ModelChoice.SelectedItem as CodexModelOption;
        var ultraFast = UltraFastChoice.IsChecked == true;
        var fast = FastChoice.IsChecked == true;
        UltraFastChoice.Visibility = model?.SupportsUltraFast == true || ultraFast ? Visibility.Visible : Visibility.Collapsed;
        FastChoice.Visibility = model?.SupportsFast == true || fast ? Visibility.Visible : Visibility.Collapsed;
        UltraFastChoice.IsEnabled = canChoose && (model?.SupportsUltraFast == true || ultraFast);
        FastChoice.IsEnabled = canChoose && (model?.SupportsFast == true || fast);
        UltraFastChoice.ToolTip = ultraFast && model?.SupportsUltraFast != true && !locked
            ? "Ultrafast is unavailable for this model/account. Turn it off or choose a supported model."
            : $"Ultrafast: {(ultraFast ? "On" : "Off")}";
        FastChoice.ToolTip = fast && model?.SupportsFast != true && !locked
            ? "Fast is unavailable for this model/account. Turn it off or choose a supported model."
            : $"Fast: {(fast ? "On" : "Off")}";
        AccessChoice.IsEnabled = canChoose && _accessAvailable;
        KeepAwakeChoice.IsEnabled = !locked;
        EffortChoice.ToolTip = (EffortChoice.SelectedItem as CodexReasoningEffortOption)?.Description;
        RetryChoicesButton.IsEnabled = canChoose;
        RetryChoicesButton.Visibility = canChoose && (!_preferencesAvailable || !_accessAvailable ||
            ModelChoice.Items.Count == 0 || EffortChoice.Items.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.IsEnabled = !IsSubmitting && !_closingContext && !_changingContextGatherer && !_receivingContextGathererDraft && !IsDictating;
        PromptBox.IsReadOnly = locked;
        MessageImages.IsEnabled = !IsSubmitting && !_receivingContextGathererDraft && !_pastingImage && !IsDictating;
        MessageImages.Tag = !locked;
        ContextBox.IsReadOnly = locked || !_preferencesAvailable;
        SaveContextButton.IsEnabled = !locked && !IsDictating && _preferencesAvailable && HasUnsavedContext;
        ContextStatus.Text = HasUnsavedContext ? "Unsaved context" :
            string.IsNullOrWhiteSpace(ContextBox.Text) ? "" : "Saved for this project";
        CloseButton.IsEnabled = !IsSubmitting && !_savingContext && !_checkingStatus && !_closingContext && !_pastingImage && !_loadingAccess &&
            !_changingContextGatherer && !_receivingContextGathererDraft && !_changingDictation && !_closingDictation;
        StartButton.IsEnabled = !locked && !IsDictating && !_loadingModels && !_loadingAccess && _accessAvailable && _preferencesAvailable && _recoveryAvailable &&
            ModelChoice.SelectedItem is CodexModelOption && EffortChoice.SelectedItem is CodexReasoningEffortOption &&
            AccessChoice.SelectedItem is AgentAccessOption &&
            (!ultraFast || model?.SupportsUltraFast == true) && (!fast || model?.SupportsFast == true) &&
            !(ultraFast && fast) &&
            (!string.IsNullOrWhiteSpace(PromptBox.Text) || _messageImages.Count > 0);
        WaitButton.IsEnabled = StartButton.IsEnabled;
        WaitButton.Content = _savingWaiting ? "Saving…" : "_Wait & next";
        StartButton.Content = IsSubmitting && !_savingWaiting ? "Starting…" : "_Start Agent";
        var showingSubmission = PendingAttemptId is not null || _composerDraft is not null;
        Title = showingSubmission ? "Agent submission" : "Start Agent";
        ChatModeTitle.Text = showingSubmission ? "Submitted Codex chat" : "New Codex chat";
        StartButton.Visibility = showingSubmission ? Visibility.Collapsed : Visibility.Visible;
        WaitButton.Visibility = StartButton.Visibility;
        CheckStatusButton.Visibility = PendingAttemptId is not null || !_recoveryAvailable ? Visibility.Visible : Visibility.Collapsed;
        CheckStatusButton.IsEnabled = !_checkingStatus && !IsSubmitting && !IsDictating;
        ReviewButton.Visibility = _pendingReceipt?.RequiresReview == true ? Visibility.Visible : Visibility.Collapsed;
        ReviewButton.IsEnabled = !_checkingStatus && !IsSubmitting && !IsDictating;
        NewChatButton.Visibility = PendingAttemptId is not null || _composerDraft is not null
            ? Visibility.Visible : Visibility.Collapsed;
        NewChatButton.IsEnabled = !IsSubmitting && !_checkingStatus && !_savingContext && !_pastingImage &&
            !_loadingAccess && !_loadingModels && !_closingContext && !_changingContextGatherer && !_receivingContextGathererDraft && !IsDictating;
        UpdateContextGathererControls();
        UpdateDictationControls();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = !SettingsPopup.IsOpen;

    private void SettingsPopup_Opened(object? sender, EventArgs e)
    {
        if (AccessChoice.IsEnabled) AccessChoice.Focus();
        else SettingsPopup.Child.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseAgentSettings();

    private void CloseAgentSettings()
    {
        SettingsPopup.IsOpen = false;
        SettingsButton.Focus();
    }

    private void SettingsPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (AccessChoice.IsDropDownOpen) AccessChoice.IsDropDownOpen = false;
        else CloseAgentSettings();
        e.Handled = true;
    }

    private async void RetryChoices_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChooseAgentSettings) return;
        CloseAgentSettings();
        if (!_preferencesAvailable)
        {
            try
            {
                _preferences = _store.Load(_projectId);
                ContextBox.Text = _preferences.Context;
                UltraFastChoice.IsChecked = _preferences.UltraFastEnabled;
                FastChoice.IsChecked = _preferences.FastEnabled;
                _preferencesAvailable = true;
            }
            catch (Exception ex)
            {
                ShowStatus("Saved agent preferences could not be loaded. Your message is kept. " +
                    SensitiveDataProtection.Redact(ex.Message), error: true);
                return;
            }
        }
        if (!_recoveryAvailable) await CheckStatusAsync();
        if (_recoveryAvailable)
        {
            await LoadAccessAsync();
            if (_accessAvailable) await LoadModelsAsync();
        }
    }

    private async void SaveContext_Click(object sender, RoutedEventArgs e) => await SaveContextAsync();

    private async Task<bool> SaveContextAsync()
    {
        if (_preferences is null || !_preferencesAvailable || _savingContext || IsSubmitting) return false;
        if (!ValidateContext()) return false;
        var candidate = _preferences with { Context = ContextBox.Text };
        _savingContext = true;
        UpdateControls();
        try
        {
            _preferences = await Task.Run(() => _store.Save(candidate));
            if (_closed) return false;
            _discardContext = false;
            ShowStatus("Project context saved.");
            return true;
        }
        catch (Exception ex)
        {
            if (!_closed) ShowStatus("Context could not be saved. Your text is kept. " +
                SensitiveDataProtection.Redact(ex.Message), error: true);
            return false;
        }
        finally
        {
            _savingContext = false;
            if (!_closed) UpdateControls();
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!StartButton.IsEnabled || !TryCaptureRequest(out var request, out var candidate)) return;
        IsSubmitting = true;
        ShowStatus("Starting a new Codex chat…");
        UpdateControls();
        try
        {
            await StopContextGathererAsync();
            // Save remembered model, access, and speed choices. Context changes require Save context.
            _preferences = await Task.Run(() => _store.Save(candidate));
            var receipt = await _client.StartAsync(request, _lifetime.Token);
            if (!_closed) ApplyReceipt(receipt);
        }
        catch (Exception ex)
        {
            if (!_closed) ShowStatus("Agent could not start. Your message is kept. " +
                SensitiveDataProtection.Redact(ex.Message), error: true);
        }
        finally
        {
            IsSubmitting = false;
            if (!_closed)
            {
                UpdateControls();
                if (PendingAttemptId is null) PromptBox.Focus();
            }
        }
    }

    private bool TryCaptureRequest(out CodexAgentChatRequest request, out CodexAgentPromptPreferences preferences)
    {
        request = null!;
        preferences = null!;
        if (IsDictating) return false;
        if (_preferences is null ||
            ModelChoice.SelectedItem is not CodexModelOption model ||
            EffortChoice.SelectedItem is not CodexReasoningEffortOption effort ||
            AccessChoice.SelectedItem is not AgentAccessOption access) return false;
        if (PromptBox.Text.Length > CodexAgentChatClient.MaximumPromptCharacters)
        {
            ShowStatus($"Message is too long ({PromptBox.Text.Length:N0} characters). Shorten it to " +
                $"{CodexAgentChatClient.MaximumPromptCharacters:N0} characters or fewer. Your full text is kept.", error: true);
            PromptBox.Focus();
            return false;
        }
        if (!ValidateContext()) return false;
        if (UltraFastChoice.IsChecked == true && !model.SupportsUltraFast ||
            FastChoice.IsChecked == true && !model.SupportsFast ||
            UltraFastChoice.IsChecked == true && FastChoice.IsChecked == true)
        {
            ShowStatus("Choose one available speed setting, or turn both off. Your message is kept.", error: true);
            return false;
        }
        // Capture the entire draft before awaiting; every start path uses this same frozen request.
        request = new CodexAgentChatRequest(_projectId, _projectName, _folder, PromptBox.Text,
            ContextBox.Text, model.Id, effort.Id)
        {
            Images = DraftImages,
            AccessMode = access.Mode,
            KeepAwake = DraftKeepAwake,
            UltraFastEnabled = DraftUltraFastEnabled,
            FastEnabled = DraftFastEnabled
        };
        preferences = _preferences with
        {
            ModelId = model.Id, ReasoningEffort = effort.Id, AccessMode = access.Mode,
            UltraFastEnabled = request.UltraFastEnabled, FastEnabled = request.FastEnabled
        };
        return true;
    }

    private void ApplyReceipt(CodexAgentChatReceipt receipt)
    {
        if (receipt.Request.ProjectId != _projectId)
            throw new InvalidOperationException("The saved submission belongs to another project. Its receipt was preserved.");
        ThreadId = receipt.ThreadId;
        KeepAwakeChoice.IsChecked = receipt.Request.KeepAwake;
        UltraFastChoice.IsChecked = receipt.Request.UltraFastEnabled;
        FastChoice.IsChecked = receipt.Request.FastEnabled;
        if (receipt.IsAccepted)
        {
            if (_composerDraft is null) SentSuccessfully = true;
            PendingAttemptId = null;
            _pendingReceipt = null;
            PromptBox.Clear();
            // The empty editor now belongs to a new draft. The submitted chat's switch lives in Crew.
            KeepAwakeChoice.IsChecked = false;
            ClearMessageImages();
            ClearFrozenModel();
            var summary = string.IsNullOrWhiteSpace(receipt.Summary) ? "Agent chat started." : receipt.Summary;
            if (receipt.Request.KeepAwake) summary += " Keep awake was requested for the primary agent. Manage it on its Crew card.";
            ShowStatus(summary,
                error: receipt.State == CodexAgentChatState.Failed);
        }
        else if (receipt.RequiresReview || receipt.SubmissionAttempted || receipt.State is CodexAgentChatState.Prepared or CodexAgentChatState.Starting)
        {
            PendingAttemptId = receipt.AttemptId;
            _pendingReceipt = receipt;
            RestoreMessageImages(receipt.Request.Images);
            if (string.IsNullOrEmpty(PromptBox.Text))
            {
                PromptBox.Text = receipt.Request.Prompt;
            }
            if (ModelChoice.Items.Count == 0 || _showingFrozenModel)
            {
                _restoringModels = true;
                try
                {
                    // This is the frozen submission choice, not a newly discovered catalog entry.
                    var effort = new CodexReasoningEffortOption(receipt.Request.ReasoningEffort, "Submitted thinking level");
                    var model = new CodexModelOption(receipt.Request.ModelId, receipt.Request.ModelId,
                        effort.Id, new[] { effort }, SupportsUltraFast: receipt.Request.UltraFastEnabled,
                        FastServiceTier: receipt.Request.FastEnabled ? "priority" : null);
                    ModelChoice.ItemsSource = new[] { model };
                    ModelChoice.SelectedItem = model;
                    SetEfforts(model, effort.Id);
                    _showingFrozenModel = true;
                }
                finally { _restoringModels = false; }
            }
            AccessChoice.SelectedItem = AccessChoice.Items.OfType<AgentAccessOption>()
                .First(option => option.Mode == receipt.Request.AccessMode);
            _showingFrozenAccess = true;
            ShowStatus((string.IsNullOrWhiteSpace(receipt.Summary) ? "Chat submission is not confirmed." : receipt.Summary) +
                " Submission is kept. Check status to inspect this request, or choose New chat for a separate conversation.", error: true);
        }
        else
        {
            PendingAttemptId = null;
            _pendingReceipt = null;
            ClearFrozenModel();
            ShowStatus(string.IsNullOrWhiteSpace(receipt.Summary) ? "Agent did not start. Your message is kept." : receipt.Summary,
                error: true);
        }
        UpdateControls();
    }

    private sealed record ComposerDraft(string Text, IReadOnlyList<CodexAgentChatImage> Images, bool KeepAwake,
        System.Collections.IEnumerable? Models, CodexModelOption? Model, CodexReasoningEffortOption? Effort,
        AgentAccessOption? Access, bool UltraFastEnabled, bool FastEnabled);

    private async Task InspectSubmissionAsync(string attemptId)
    {
        if (_closed || !CanChooseAgentSettings || _closingContext) return;
        _checkingStatus = true;
        UpdateControls();
        try
        {
            await StopContextGathererAsync();
            var receipt = await Task.Run(() => _client.ReadReceipt(attemptId));
            if (_closed) return;
            if (receipt is null)
            {
                ShowStatus("No unresolved agent submissions for this project.");
                return;
            }
            if (receipt.Request.ProjectId != _projectId)
                throw new InvalidOperationException("The saved submission belongs to another project. Its receipt was preserved.");
            _composerDraft = new(PromptBox.Text, DraftImages, KeepAwakeChoice.IsChecked == true,
                ModelChoice.ItemsSource, ModelChoice.SelectedItem as CodexModelOption,
                EffortChoice.SelectedItem as CodexReasoningEffortOption, AccessChoice.SelectedItem as AgentAccessOption,
                UltraFastChoice.IsChecked == true, FastChoice.IsChecked == true);
            _previousAttemptId = receipt.AttemptId;
            PromptBox.Clear();
            ModelChoice.ItemsSource = null;
            ApplyReceipt(receipt);
        }
        catch (Exception ex)
        {
            if (!_closed) ShowStatus("Previous submission could not be read. Your draft is kept. " +
                SensitiveDataProtection.Redact(ex.Message), error: true);
        }
        finally
        {
            _checkingStatus = false;
            if (!_closed) UpdateControls();
        }
    }

    private async void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (NewChatButton.IsEnabled) await RestoreComposerAsync();
    }

    private async Task RestoreComposerAsync()
    {
        _previousAttemptId = PendingAttemptId ?? _previousAttemptId;
        PendingAttemptId = null;
        _pendingReceipt = null;
        _recoveryAvailable = true;
        ClearFrozenModel();
        var draft = _composerDraft;
        _composerDraft = null;
        PromptBox.Text = draft?.Text ?? "";
        RestoreMessageImages(draft?.Images ?? []);
        KeepAwakeChoice.IsChecked = draft?.KeepAwake == true;
        UltraFastChoice.IsChecked = draft?.UltraFastEnabled ?? _preferences?.UltraFastEnabled ?? false;
        FastChoice.IsChecked = draft?.FastEnabled ?? _preferences?.FastEnabled ?? false;
        if (draft is not null)
        {
            _restoringModels = true;
            try
            {
                ModelChoice.ItemsSource = draft.Models;
                ModelChoice.SelectedItem = draft.Model;
                SetEfforts(draft.Model, draft.Effort?.Id ?? "");
                AccessChoice.SelectedItem = draft.Access;
                _accessAvailable = draft.Access is not null;
            }
            finally { _restoringModels = false; }
        }
        ClearStatus();
        UpdateControls();
        if (_preferencesAvailable)
        {
            await LoadAccessAsync();
            if (_accessAvailable && ModelChoice.Items.Count == 0) await LoadModelsAsync();
        }
        if (!_closed) PromptBox.Focus();
    }

    private void ClearFrozenModel()
    {
        if (_showingFrozenAccess)
        {
            _restoringModels = true;
            try
            {
                AccessChoice.SelectedItem = null;
                _accessAvailable = false;
                _showingFrozenAccess = false;
            }
            finally { _restoringModels = false; }
        }
        if (!_showingFrozenModel) return;
        _restoringModels = true;
        try
        {
            ModelChoice.ItemsSource = null;
            EffortChoice.ItemsSource = null;
            _showingFrozenModel = false;
        }
        finally { _restoringModels = false; }
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingReceipt is not { RequiresReview: true } receipt || _checkingStatus || IsSubmitting) return;
        var choice = MessageBox.Show(this,
            "Submission outcome is unknown. Review this chat in Codex, or recent tasks if no chat ID was recorded.\n\n" +
            $"Chat ID: {receipt.ThreadId ?? "Not recorded"}\nTurn ID: {receipt.TurnId ?? "Not recorded"}\n\n" +
            "Have you confirmed the original request is no longer active and want to prepare a separate new chat?\n\n" +
            "Yes keeps the saved submission receipt and returns to the new chat draft. It does not send another message.",
            "Review agent submission", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes) return;
        _checkingStatus = true;
        UpdateControls();
        try
        {
            await Task.Run(() => _client.AcknowledgeReview(receipt.AttemptId));
            if (_closed) return;
            PendingAttemptId = null;
            _pendingReceipt = null;
            _previousAttemptId = null;
            _recoveryAvailable = true;
        }
        catch (Exception ex)
        {
            if (!_closed) ShowStatus("Submission review could not be saved. " +
                SensitiveDataProtection.Redact(ex.Message), error: true);
        }
        finally
        {
            _checkingStatus = false;
            if (!_closed) UpdateControls();
        }
        if (!_closed && PendingAttemptId is null)
        {
            await RestoreComposerAsync();
            ShowStatus("Submission review recorded.");
        }
    }

    private async void CheckStatus_Click(object sender, RoutedEventArgs e)
    {
        await CheckStatusAsync();
        if (!_closed && PendingAttemptId is null && _composerDraft is null && _preferencesAvailable &&
            _recoveryAvailable && ModelChoice.Items.Count == 0)
        {
            await LoadAccessAsync();
            if (_accessAvailable) await LoadModelsAsync();
        }
    }

    private async Task CheckStatusAsync()
    {
        if (_checkingStatus || PendingAttemptId is null) return;
        var attemptId = PendingAttemptId;
        _checkingStatus = true;
        UpdateControls();
        try
        {
            var receipt = await Task.Run(() => _client.ReadReceipt(attemptId));
            if (_closed) return;
            _recoveryAvailable = true;
            ApplyReceipt(receipt);
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _recoveryAvailable = false;
                ShowStatus("Submission status could not be read. Your message is kept. " +
                    SensitiveDataProtection.Redact(ex.Message), error: true);
            }
        }
        finally
        {
            _checkingStatus = false;
            if (!_closed) UpdateControls();
        }
    }

    private void ShowStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = error ? new SolidColorBrush(Color.FromRgb(245, 161, 165))
            : (Brush)FindResource("MutedBrush");
        StatusText.Visibility = Visibility.Visible;
    }

    private bool ValidateContext()
    {
        if (ContextBox.Text.Length <= CodexAgentPromptStore.MaximumContextCharacters) return true;
        ContextExpander.IsExpanded = true;
        SettingsPopup.IsOpen = true;
        ShowStatus($"Project context is too long ({ContextBox.Text.Length:N0} characters). Shorten it to " +
            $"{CodexAgentPromptStore.MaximumContextCharacters:N0} characters or fewer. Your full text is kept.", error: true);
        _ = Dispatcher.BeginInvoke(new Action(() => ContextBox.Focus()));
        return false;
    }

    private void ClearStatus()
    {
        StatusText.Text = "";
        StatusText.Visibility = Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (IsSubmitting || _savingContext || _checkingStatus || _closingContext || _pastingImage || _loadingAccess ||
            _changingContextGatherer || _receivingContextGathererDraft || _changingDictation || _closingDictation)
        {
            e.Cancel = true;
            return;
        }
        if (_dictationSession is not null)
        {
            e.Cancel = true;
            _replacementClosePending = true;
            _closingDictation = true;
            var stopped = false;
            try { stopped = await StopDictationAsync(); }
            finally
            {
                _closingDictation = false;
                if (!stopped) _replacementClosePending = false;
                UpdateControls();
            }
            if (stopped) _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                try { Close(); }
                finally { _replacementClosePending = false; }
            }));
            return;
        }
        if (_dashboardResumeClose) return;
        if (!HasUnsavedContext) return;
        e.Cancel = true;
        _replacementClosePending = true;
        _closingContext = true;
        var resumeClose = false;
        UpdateControls();
        try
        {
            var choice = MessageBox.Show(this, "Save project context before closing?\n\nYour message draft is kept.",
                "Unsaved project context", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes && !await SaveContextAsync()) return;
            if (choice == MessageBoxResult.No) _discardContext = true;
            resumeClose = true;
        }
        finally
        {
            _closingContext = false;
            if (!resumeClose) _replacementClosePending = false;
            UpdateControls();
        }
        // The synchronous Discard branch still runs inside WPF's Closing event.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            try { Close(); }
            finally { _replacementClosePending = false; }
        }));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SettingsPopup.IsOpen)
            {
                SettingsPopup_PreviewKeyDown(sender, e);
                return;
            }
            if (!IsSubmitting && !_savingContext) Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (StartButton.IsEnabled) Start_Click(StartButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
