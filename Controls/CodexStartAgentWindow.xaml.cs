using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

/// <summary>A project-scoped prompt draft and explicit, independent Codex chat submission.</summary>
public partial class CodexStartAgentWindow : Window
{
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
    private bool _recoveryAvailable;
    private bool _showingFrozenModel;
    private bool _loadingAccess;
    private bool _accessAvailable;
    private bool _showingFrozenAccess;
    private CodexAgentChatReceipt? _pendingReceipt;

    public string Draft => PromptBox.Text;
    public bool HasUnsavedContext => !_discardContext && _preferences is not null &&
        !string.Equals(ContextBox.Text, _preferences.Context, StringComparison.Ordinal);
    public bool IsSubmitting { get; private set; }
    public string? PendingAttemptId { get; private set; }
    public bool SentSuccessfully { get; private set; }
    public string? ThreadId { get; private set; }

    public CodexStartAgentWindow(string projectId, string projectName, string folder, string draft = "",
        string? pendingAttemptId = null, IReadOnlyList<CodexAgentChatImage>? draftImages = null)
    {
        _projectId = projectId;
        _projectName = projectName;
        _folder = folder;
        PendingAttemptId = pendingAttemptId;
        InitializeComponent();
        ProjectTitle.Text = projectName;
        ProjectTitle.ToolTip = folder;
        PromptBox.Text = draft;
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
        _initializing = false;
        UpdateControls();
        Loaded += async (_, _) =>
        {
            PromptBox.Focus();
            PromptBox.CaretIndex = PromptBox.Text.Length;
            await CheckStatusAsync();
            if (!_closed && PendingAttemptId is null && _recoveryAvailable && _preferencesAvailable)
            {
                await LoadAccessAsync();
                if (_accessAvailable) await LoadModelsAsync();
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    private async Task LoadModelsAsync()
    {
        if (_loadingModels || IsSubmitting || PendingAttemptId is not null) return;
        _loadingModels = true;
        var wantedModel = (ModelChoice.SelectedItem as CodexModelOption)?.Id ?? _preferences?.ModelId ?? "";
        var wantedEffort = (EffortChoice.SelectedItem as CodexReasoningEffortOption)?.Id ?? _preferences?.ReasoningEffort ?? "";
        ShowStatus("Loading Codex models…");
        UpdateControls();
        try
        {
            var models = await new CodexModelCatalog().LoadAsync(_lifetime.Token);
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

    private sealed record AgentAccessOption(CodexAgentAccessMode Mode, string Label);

    private async Task LoadAccessAsync()
    {
        if (_loadingAccess || IsSubmitting || PendingAttemptId is not null || _accessAvailable) return;
        _loadingAccess = true;
        UpdateControls();
        try
        {
            var mode = _preferences?.AccessMode ??
                await CodexAgentAccessPolicy.LoadConfiguredDefaultAsync(_folder, _lifetime.Token);
            if (_closed) return;
            AccessChoice.SelectedItem = AccessChoice.Items.OfType<AgentAccessOption>().First(option => option.Mode == mode);
            _accessAvailable = true;
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (!_closed) ShowStatus("Agent access could not be loaded. Refresh to retry. " +
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
        var locked = IsSubmitting || _checkingStatus || _savingContext || _pastingImage || PendingAttemptId is not null;
        var canChoose = !locked && !_loadingModels && !_loadingAccess;
        ModelChoice.IsEnabled = canChoose;
        EffortChoice.IsEnabled = canChoose;
        AccessChoice.IsEnabled = canChoose && _accessAvailable;
        EffortChoice.ToolTip = (EffortChoice.SelectedItem as CodexReasoningEffortOption)?.Description;
        RefreshButton.IsEnabled = canChoose;
        PromptBox.IsReadOnly = locked;
        MessageImages.IsEnabled = !locked;
        ContextBox.IsReadOnly = locked || !_preferencesAvailable;
        SaveContextButton.IsEnabled = !locked && _preferencesAvailable && HasUnsavedContext;
        ContextStatus.Text = HasUnsavedContext ? "Unsaved context" :
            string.IsNullOrWhiteSpace(ContextBox.Text) ? "" : "Saved for this project";
        CloseButton.IsEnabled = !IsSubmitting && !_savingContext && !_checkingStatus && !_closingContext && !_pastingImage && !_loadingAccess;
        StartButton.IsEnabled = !locked && !_loadingModels && !_loadingAccess && _accessAvailable && _preferencesAvailable && _recoveryAvailable &&
            ModelChoice.SelectedItem is CodexModelOption && EffortChoice.SelectedItem is CodexReasoningEffortOption &&
            AccessChoice.SelectedItem is AgentAccessOption &&
            (!string.IsNullOrWhiteSpace(PromptBox.Text) || _messageImages.Count > 0);
        StartButton.Content = IsSubmitting ? "Starting…" : "_Start Agent";
        CheckStatusButton.Visibility = PendingAttemptId is not null || !_recoveryAvailable ? Visibility.Visible : Visibility.Collapsed;
        CheckStatusButton.IsEnabled = !_checkingStatus && !IsSubmitting;
        ReviewButton.Visibility = _pendingReceipt?.RequiresReview == true ? Visibility.Visible : Visibility.Collapsed;
        ReviewButton.IsEnabled = !_checkingStatus && !IsSubmitting;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_preferencesAvailable)
        {
            try
            {
                _preferences = _store.Load(_projectId);
                ContextBox.Text = _preferences.Context;
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
        if (!StartButton.IsEnabled || _preferences is null ||
            ModelChoice.SelectedItem is not CodexModelOption model ||
            EffortChoice.SelectedItem is not CodexReasoningEffortOption effort ||
            AccessChoice.SelectedItem is not AgentAccessOption access) return;
        if (PromptBox.Text.Length > CodexAgentChatClient.MaximumPromptCharacters)
        {
            ShowStatus($"Message is too long ({PromptBox.Text.Length:N0} characters). Shorten it to " +
                $"{CodexAgentChatClient.MaximumPromptCharacters:N0} characters or fewer. Your full text is kept.", error: true);
            PromptBox.Focus();
            return;
        }
        if (!ValidateContext()) return;
        var prompt = PromptBox.Text;
        var context = ContextBox.Text;
        var candidate = _preferences with { ModelId = model.Id, ReasoningEffort = effort.Id, AccessMode = access.Mode };
        IsSubmitting = true;
        ShowStatus("Starting a new Codex chat…");
        UpdateControls();
        try
        {
            // Save only the remembered model and access choices here. Context changes require Save context.
            _preferences = await Task.Run(() => _store.Save(candidate));
            var request = new CodexAgentChatRequest(_projectId, _projectName, _folder, prompt,
                context, model.Id, effort.Id) { Images = DraftImages, AccessMode = access.Mode };
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

    private void ApplyReceipt(CodexAgentChatReceipt receipt)
    {
        ThreadId = receipt.ThreadId;
        if (receipt.IsAccepted)
        {
            SentSuccessfully = true;
            PendingAttemptId = null;
            _pendingReceipt = null;
            PromptBox.Clear();
            ClearMessageImages();
            ClearFrozenModel();
            ShowStatus(string.IsNullOrWhiteSpace(receipt.Summary) ? "Agent chat started." : receipt.Summary,
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
                if (!HasUnsavedContext) ContextBox.Text = receipt.Request.Context;
            }
            if (ModelChoice.Items.Count == 0 || _showingFrozenModel)
            {
                _restoringModels = true;
                try
                {
                    // This is the frozen submission choice, not a newly discovered catalog entry.
                    var effort = new CodexReasoningEffortOption(receipt.Request.ReasoningEffort, "Submitted thinking level");
                    var model = new CodexModelOption(receipt.Request.ModelId, receipt.Request.ModelId,
                        effort.Id, new[] { effort });
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
                " Your message is kept. Check status before starting another chat.", error: true);
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
            "Yes keeps the saved submission receipt and clears this message editor. It does not send another message.",
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
            _recoveryAvailable = true;
            PromptBox.Clear();
            ClearMessageImages();
            ClearFrozenModel();
            ShowStatus("Submission review recorded. Write a message to start a separate new chat.");
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
        if (!_closed && PendingAttemptId is null && _preferencesAvailable)
        {
            await LoadAccessAsync();
            if (_accessAvailable) await LoadModelsAsync();
        }
    }

    private async void CheckStatus_Click(object sender, RoutedEventArgs e)
    {
        await CheckStatusAsync();
        if (!_closed && PendingAttemptId is null && _preferencesAvailable && _recoveryAvailable && ModelChoice.Items.Count == 0)
        {
            await LoadAccessAsync();
            if (_accessAvailable) await LoadModelsAsync();
        }
    }

    private async Task CheckStatusAsync()
    {
        if (_checkingStatus) return;
        var attemptId = PendingAttemptId;
        _checkingStatus = true;
        UpdateControls();
        try
        {
            var receipt = await Task.Run(() => attemptId is null
                ? _client.FindPendingReceipt(_projectId) : _client.ReadReceipt(attemptId));
            if (_closed) return;
            _recoveryAvailable = true;
            if (receipt is null && attemptId is not null)
                ShowStatus("The saved submission could not be found. Your message is kept; submission remains locked to prevent a duplicate chat.", error: true);
            else if (receipt is not null) ApplyReceipt(receipt);
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
        ShowStatus($"Project context is too long ({ContextBox.Text.Length:N0} characters). Shorten it to " +
            $"{CodexAgentPromptStore.MaximumContextCharacters:N0} characters or fewer. Your full text is kept.", error: true);
        ContextBox.Focus();
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
        if (IsSubmitting || _savingContext || _checkingStatus || _closingContext || _pastingImage || _loadingAccess)
        {
            e.Cancel = true;
            return;
        }
        if (!HasUnsavedContext) return;
        e.Cancel = true;
        _closingContext = true;
        UpdateControls();
        try
        {
            var choice = MessageBox.Show(this, "Save project context before closing?\n\nYour message draft is kept.",
                "Unsaved project context", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes && !await SaveContextAsync()) return;
            if (choice == MessageBoxResult.No) _discardContext = true;
        }
        finally
        {
            _closingContext = false;
            UpdateControls();
        }
        // The synchronous Discard branch still runs inside WPF's Closing event.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
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
