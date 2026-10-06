using System.Text.Json;
using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private LongRunningTaskMode? _longRunningTaskMode;
    private QueueDashboardPermit? _queueDashboardPermit;
    public bool LongRunningTaskEnabled => _longRunningTaskMode?.IsEnabled == true;
    public string LongRunningTaskDescription =>
        "Keep the computer and display awake while this dashboard is open, including when minimized. " +
        "Gently nudge the pointer after about one minute without input. " +
        "New queued tasks require this switch and their queue to be enabled. " +
        "Uncheck or close the dashboard to hold later queued tasks; started tasks continue.\n\n" + (_longRunningTaskMode?.Status ?? "Off.");

    private void InitializeLongRunningTaskMode()
    {
        _longRunningTaskMode = new LongRunningTaskMode(Dispatcher);
        _queueDashboardPermit = new QueueDashboardPermit(_store.SettingsPath, Dispatcher);
        _longRunningTaskMode.StatusChanged += () => Changed(nameof(LongRunningTaskDescription));
        Closed += (_, _) =>
        {
            try { _queueDashboardPermit.Dispose(); }
            finally { _longRunningTaskMode.Dispose(); }
        };
        if (!_settings.LongRunningTaskEnabled) return;
        try { SetLongRunningTaskEnabled(true); }
        catch (InvalidOperationException ex) { Notice = ex.Message; }
        Changed(nameof(LongRunningTaskEnabled));
    }

    private void LongRunningTask_Click(object sender, RoutedEventArgs e)
    {
        if (_closeRequested || _closing || _longRunningTaskMode is null)
        {
            Changed(nameof(LongRunningTaskEnabled));
            return;
        }
        var enabled = !LongRunningTaskEnabled;
        try
        {
            SetLongRunningTaskEnabled(enabled);
            try
            {
                // Save a detached candidate to retain all project/layout selections.
                var candidate = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(_settings))!;
                candidate.LongRunningTaskEnabled = enabled;
                _store.Save(candidate);
                _settings.LongRunningTaskEnabled = enabled;
                Notice = enabled
                    ? "Long Running Task is on. Enabled queues can start tasks while this dashboard stays open."
                    : "Long Running Task is off. Later queued tasks wait; started tasks continue.";
            }
            catch (Exception)
            {
                // In particular, a failed settings save must never prevent switching
                // the feature off now. The stored choice remains unchanged.
                Notice = $"Long Running Task is {(enabled ? "on" : "off")} for this session, but its preference could not be saved. Queue starts follow this session's switch. Reopening will use the previously saved choice.";
            }
        }
        catch (InvalidOperationException ex) { Notice = ex.Message; }
        finally
        {
            // Refresh even after failure: the checkbox always reflects the live request.
            Changed(nameof(LongRunningTaskEnabled));
            Changed(nameof(LongRunningTaskDescription));
        }
    }

    private void SetLongRunningTaskEnabled(bool enabled)
    {
        if (enabled)
        {
            _longRunningTaskMode!.SetEnabled(true);
            try { _queueDashboardPermit!.SetEnabled(true); }
            catch
            {
                _longRunningTaskMode.SetEnabled(false);
                throw;
            }
        }
        else
        {
            _longRunningTaskMode!.SetEnabled(false);
            _queueDashboardPermit!.SetEnabled(false);
        }
    }
}
