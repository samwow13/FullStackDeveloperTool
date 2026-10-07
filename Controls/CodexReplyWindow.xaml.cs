using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

/// <summary>A fixed chat inbox, durable receipts, and an in-memory unsent draft.</summary>
public partial class CodexReplyWindow : Window
{
    private readonly string _threadId;
    private readonly CodexReplyInboxStore _inbox;
    private readonly DispatcherTimer _receiptTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _sending;
    private bool _refreshing;
    private bool _closed;
    private string? _lastSavedReplyId;
    public string Draft => ResponseBox.Text;
    public string ReplyId { get; private set; }
    public bool RetryLocked { get; private set; }

    public CodexReplyWindow(string threadId, string title, string draft, string replyId, bool retryLocked,
        string agentMessage = "")
    {
        _threadId = threadId;
        _inbox = new CodexReplyInboxStore(new SettingsStore().SettingsPath);
        ReplyId = replyId;
        RetryLocked = retryLocked;
        InitializeComponent();
        ChatTitle.Text = title;
        ChatTitle.ToolTip = $"{title}\nChat ID: {threadId}";
        if (!string.IsNullOrWhiteSpace(agentMessage) && agentMessage != "Reading saved chat…" && agentMessage != title)
        {
            AgentMessage.Text = agentMessage;
            AgentMessageScroll.Visibility = Visibility.Visible;
        }
        ResponseBox.Text = draft;
        ResponseBox.IsReadOnly = retryLocked;
        if (retryLocked) ShowError("Save could not be confirmed. Retry this same response; its reply ID prevents duplicate saves.");
        UpdateSendState();
        _receiptTimer.Tick += async (_, _) => await RefreshReceiptsAsync();
        Loaded += async (_, _) =>
        {
            ResponseBox.Focus();
            ResponseBox.CaretIndex = ResponseBox.Text.Length;
            _receiptTimer.Start();
            await RefreshReceiptsAsync();
        };
        Closed += (_, _) => { _closed = true; _receiptTimer.Stop(); };
    }

    private void Response_Changed(object sender, TextChangedEventArgs e)
    {
        if (SendButton is not null) UpdateSendState();
    }

    private void UpdateSendState()
    {
        SendButton.IsEnabled = !_sending && !string.IsNullOrWhiteSpace(ResponseBox.Text);
        SendButton.Content = _sending ? "Saving…" : RetryLocked ? "_Retry save" : "_Send to inbox";
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (!SendButton.IsEnabled) return;
        _sending = true;
        ResponseBox.IsReadOnly = true;
        CancelButton.IsEnabled = false;
        DeliveryStatus.Visibility = Visibility.Collapsed;
        UpdateSendState();
        try
        {
            await _inbox.SaveAsync(_threadId, ReplyId, ResponseBox.Text, CancellationToken.None);
            _lastSavedReplyId = ReplyId;
            ReplyId = Guid.NewGuid().ToString("D");
            RetryLocked = false;
            ResponseBox.Clear();
            await RefreshReceiptsAsync();
        }
        catch (ArgumentException ex)
        {
            RetryLocked = false;
            ShowError(ex.Message);
        }
        catch (CodexReplyInboxConflictException ex)
        {
            // Capacity/validation/concurrent-write rejection happens before commit; retain editable text.
            RetryLocked = false;
            ShowError(ex.Message);
        }
        catch (Exception)
        {
            // Keep the exact ID/text across retry, including an uncertain atomic file replacement.
            RetryLocked = true;
            ShowError("Could not confirm inbox save. Your response is kept. Retry this same response; its reply ID prevents duplicate saves.");
        }
        finally
        {
            _sending = false;
            ResponseBox.IsReadOnly = RetryLocked;
            CancelButton.IsEnabled = true;
            UpdateSendState();
            ResponseBox.Focus();
        }
    }

    private async Task RefreshReceiptsAsync()
    {
        if (_refreshing || _closed) return;
        _refreshing = true;
        try
        {
            var status = await _inbox.GetStatusAsync(_threadId, CancellationToken.None);
            if (_closed) return;
            var receipt = _lastSavedReplyId is null ? status.Receipts.FirstOrDefault()
                : status.Receipts.FirstOrDefault(item => item.ReplyId == _lastSavedReplyId);
            var latest = receipt is null ? "" : $" · Last reply: {StateText(receipt.State)}";
            InboxStatus.Text = $"Inbox: {status.PendingCount} pending · {status.ReadCount} read · {status.AcknowledgedCount} acknowledged{latest}";
        }
        catch (Exception)
        {
            if (!_closed) InboxStatus.Text = "Could not read inbox receipts. Retrying…";
        }
        finally { _refreshing = false; }
    }

    private static string StateText(CodexReplyInboxState state) => state switch
    {
        CodexReplyInboxState.Pending => "pending",
        CodexReplyInboxState.Read => "read",
        CodexReplyInboxState.Acknowledged => "acknowledged",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private void ShowError(string message)
    {
        DeliveryStatus.Text = message;
        DeliveryStatus.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_sending) e.Cancel = true;
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!_sending) Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (SendButton.IsEnabled) Send_Click(SendButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
