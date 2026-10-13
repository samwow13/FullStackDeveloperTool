using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

/// <summary>Read-only review of saved prompts and their exact submission recovery identities.</summary>
public partial class CodexWaitingAgentsWindow : Window, IDashboardReplacementWindow
{
    private readonly CodexAgentWaitingStore _store = new();
    private bool _reading;
    private bool _removing;
    private bool _reviewing;
    private bool _closed;
    private int _previewLoads;
    private int _selectionVersion;
    private IReadOnlyList<WaitingRow> _rows = [];

    public bool IsDashboardReplacementReady => !_reading && !_removing && !_reviewing && _previewLoads == 0;
    public bool IsDashboardReplacementClosePending => false;
    public event Action<CodexAgentWaitingItem>? SubmissionReviewRequested;

    public CodexWaitingAgentsWindow()
    {
        WindowTheme.Attach(this);
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        MaxHeight = SystemParameters.WorkArea.Height;
        Loaded += async (_, _) => await ReloadAsync();
        Closed += (_, _) => { _closed = true; _selectionVersion++; };
    }

    public async Task ReloadAsync()
    {
        if (_closed || _reading || _removing) return;
        var selectedId = (WaitingList.SelectedItem as WaitingRow)?.Item.Id;
        _reading = true;
        UpdateControls();
        ShowStatus("Loading saved prompts…");
        try
        {
            var items = await Task.Run(_store.List);
            if (_closed) return;
            _rows = items.Select(item => new WaitingRow(item)).ToArray();
            WaitingList.ItemsSource = _rows;
            WaitingList.SelectedItem = _rows.FirstOrDefault(row => row.Item.Id == selectedId) ?? _rows.FirstOrDefault();
            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var waiting = _rows.Count(row => row.Item.IsWaiting);
            var review = _rows.Count - waiting;
            CountText.Text = $"{waiting} waiting" + (review == 0 ? "" : $" · {review} need submission review");
            ClearStatus();
        }
        catch (Exception ex)
        {
            if (!_closed) ShowStatus("Saved prompts could not be loaded. " + SensitiveDataProtection.Redact(ex.Message), error: true);
        }
        finally
        {
            _reading = false;
            if (!_closed) UpdateControls();
        }
    }

    private async void WaitingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var version = ++_selectionVersion;
        if (!_reading && !_removing) ClearStatus();
        ImageList.ItemsSource = null;
        if (WaitingList.SelectedItem is not WaitingRow row)
        {
            DetailsPanel.Visibility = Visibility.Collapsed;
            UpdateControls();
            return;
        }
        var request = row.Item.Request;
        DetailsPanel.Visibility = Visibility.Visible;
        ProjectTitle.Text = request.ProjectName;
        SavedAtText.Text = "Saved " + row.Item.CreatedAt.ToLocalTime().ToString("g");
        PromptText.Text = request.Prompt;
        ContextText.Text = request.Context;
        SettingsText.Text = $"Model: {request.ModelId} · Thinking: {request.ReasoningEffort}\n" +
            $"Access: {(request.AccessMode == CodexAgentAccessMode.FullAccess ? "Full access" : "Workspace")} · " +
            $"Keep awake: {(request.KeepAwake ? "On" : "Off")}";
        FolderText.Text = request.Folder;
        SummaryText.Text = row.Item.IsWaiting ? "" : row.Item.Summary;
        SummaryText.Visibility = string.IsNullOrWhiteSpace(SummaryText.Text) ? Visibility.Collapsed : Visibility.Visible;
        UpdateControls();
        if (request.Images.Count == 0) return;
        _previewLoads++;
        UpdateControls();
        try
        {
            var images = await Task.Run(() => request.Images.Select(image => new SavedImage(image,
                CodexAgentImageStaging.CreatePreview(image))).ToArray());
            if (!_closed && version == _selectionVersion) ImageList.ItemsSource = images;
        }
        catch (Exception ex)
        {
            if (!_closed && version == _selectionVersion)
                ShowStatus("The saved screenshots could not be displayed. The saved prompt and attachments are kept. " +
                    SensitiveDataProtection.Redact(ex.Message), error: true);
        }
        finally
        {
            _previewLoads--;
            if (!_closed) UpdateControls();
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!RemoveButton.IsEnabled || WaitingList.SelectedItem is not WaitingRow row) return;
        if (!row.Item.IsWaiting && MessageBox.Show(this,
            "Remove this prompt from the waiting list? Codex work may still start or run. Saved submission records remain.",
            "Remove from waiting list", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _removing = true;
        UpdateControls();
        ShowStatus("Removing saved prompt…");
        var removed = false;
        try
        {
            await Task.Run(() =>
            {
                if (row.Item.IsWaiting) _store.Remove(row.Item.Id);
                else _store.Dismiss(row.Item.Id);
            });
            removed = true;
        }
        catch (Exception ex)
        {
            if (!_closed) ShowStatus("The saved prompt could not be removed. " + SensitiveDataProtection.Redact(ex.Message), error: true);
        }
        finally
        {
            _removing = false;
            if (!_closed) UpdateControls();
        }
        if (removed && !_closed) await ReloadAsync();
    }

    private async void ReviewSubmission_Click(object sender, RoutedEventArgs e)
    {
        if (!ReviewButton.IsEnabled || WaitingList.SelectedItem is not WaitingRow row || row.Item.IsWaiting ||
            string.IsNullOrWhiteSpace(row.Item.AttemptId)) return;
        _reviewing = true;
        UpdateControls();
        var reviewed = false;
        try
        {
            SubmissionReviewRequested?.Invoke(row.Item);
            reviewed = true;
        }
        catch (Exception ex)
        {
            if (!_closed) ShowStatus("The saved submission could not open. " + SensitiveDataProtection.Redact(ex.Message), error: true);
        }
        finally
        {
            _reviewing = false;
            if (!_closed) UpdateControls();
        }
        if (reviewed && !_closed) await ReloadAsync();
    }

    private void ReviewImage_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _reading || _removing || _reviewing || sender is not Button { Tag: CodexAgentChatImage image }) return;
        _reviewing = true;
        UpdateControls();
        try
        {
            new AgentImageReviewWindow(image, canEdit: false) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowStatus("The screenshot could not open. The saved prompt and attachments are kept. " +
                SensitiveDataProtection.Redact(ex.Message), error: true);
        }
        finally
        {
            _reviewing = false;
            if (!_closed) UpdateControls();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (IsDashboardReplacementReady) return;
        e.Cancel = true;
        ShowStatus("Wait for the current saved-prompt operation to finish before closing.");
    }

    private void UpdateControls()
    {
        var ready = IsDashboardReplacementReady;
        var item = (WaitingList.SelectedItem as WaitingRow)?.Item;
        RefreshButton.IsEnabled = ready;
        WaitingList.IsEnabled = !_reading && !_removing && !_reviewing;
        RemoveButton.Content = item is { IsWaiting: false } ? "Remove from list…" : "Remove selected";
        RemoveButton.IsEnabled = ready && item is not null;
        ReviewButton.Visibility = item is { IsWaiting: false } ? Visibility.Visible : Visibility.Collapsed;
        ReviewButton.IsEnabled = ready && item is { IsWaiting: false } && !string.IsNullOrWhiteSpace(item.AttemptId)
            && SubmissionReviewRequested is not null;
        CloseButton.IsEnabled = ready;
    }

    private void ShowStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? new SolidColorBrush(Color.FromRgb(255, 167, 167)) :
            (Brush)FindResource("MutedBrush");
        StatusText.Visibility = Visibility.Visible;
    }

    private void ClearStatus()
    {
        StatusText.Text = "";
        StatusText.Visibility = Visibility.Collapsed;
    }

    private sealed record WaitingRow(CodexAgentWaitingItem Item)
    {
        public string ProjectName => Item.Request.ProjectName;
        public string Folder => Item.Request.Folder;
        public string PromptTitle => Item.Request.Prompt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "Saved prompt";
        public string StateLabel => Item.IsWaiting ? "Waiting" : "Review submission";
    }

    private sealed record SavedImage(CodexAgentChatImage Attachment, BitmapSource Preview)
    {
        public string Caption => string.IsNullOrWhiteSpace(Attachment.Caption) ? "Screenshot" : Attachment.Caption;
    }
}
