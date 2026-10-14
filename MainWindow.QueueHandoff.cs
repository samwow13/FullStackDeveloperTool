using System.Windows;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private bool _queueHandoffNeedsRetry;
    private bool _queueHandoffRetryBusy;
    public bool HasQueueHandoffFailure => _queueHandoffNeedsRetry;
    public bool CanRetryQueueHandoff => _queueHandoffNeedsRetry && !_queueHandoffRetryBusy && !_closeRequested && !_closing && !_closed &&
        !_checkingStartupServices && !_savingProjectEdits && _gitProjectConnectionLease is null;
    private bool CanResumeQueueDispatch => LongRunningTaskEnabled && !_queueHandoffNeedsRetry && !_closeRequested && !_closing && !_closed &&
        !_checkingStartupServices && !_savingProjectEdits && _gitProjectConnectionLease is null;

    private void SetQueueHandoffFailure(bool failed)
    {
        _queueHandoffNeedsRetry = failed;
        Changed(nameof(HasQueueHandoffFailure));
        Changed(nameof(CanRetryQueueHandoff));
    }

    private async Task<bool> CompleteQueueHandoffAsync()
    {
        try
        {
            await QueueOwnerClient.CompleteDashboardHandoffAsync();
            SetQueueHandoffFailure(false);
            _queueDashboardPermit?.SetEnabled(CanResumeQueueDispatch);
            return true;
        }
        catch (Exception ex)
        {
            SetQueueHandoffFailure(true);
            _queueDashboardPermit?.SetEnabled(false);
            Notice = "Notes queue handoff could not finish. New dispatch remains paused; existing work and receipts are kept. " +
                SensitiveDataProtection.Redact(ex.Message) + " Use Functions → Retry queue handoff after resolving the cause.";
            return false;
        }
    }

    private async void RetryQueueHandoff_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRetryQueueHandoff) return;
        _queueHandoffRetryBusy = true;
        Changed(nameof(CanRetryQueueHandoff));
        Notice = "Checking Notes queue ownership…";
        try
        {
            if (await CompleteQueueHandoffAsync())
                Notice = "Queue handoff recovery finished. Existing queue intent is kept; no task was resubmitted.";
        }
        finally { _queueHandoffRetryBusy = false; Changed(nameof(CanRetryQueueHandoff)); }
    }
}
