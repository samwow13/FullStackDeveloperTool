using System.Text.Json;
using System.Windows;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private LongRunningTaskMode? _longRunningTaskMode;
    public bool LongRunningTaskEnabled => _longRunningTaskMode?.IsEnabled == true;
    public string LongRunningTaskDescription =>
        "Keep the computer and display awake while this dashboard is open, including when minimized. " +
        "Gently nudge the pointer after about one minute without input. Teams controls its own presence; Active is not guaranteed. " +
        "Uncheck or close the dashboard to stop.\n\n" + (_longRunningTaskMode?.Status ?? "Off.");

    private void InitializeLongRunningTaskMode()
    {
        _longRunningTaskMode = new LongRunningTaskMode(Dispatcher);
        _longRunningTaskMode.StatusChanged += () => Changed(nameof(LongRunningTaskDescription));
        Closed += (_, _) => _longRunningTaskMode.Dispose();
        if (!_settings.LongRunningTaskEnabled) return;
        try { _longRunningTaskMode.SetEnabled(true); }
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
            _longRunningTaskMode.SetEnabled(enabled);
            try
            {
                // Save a detached candidate to retain all project/layout selections.
                var candidate = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(_settings))!;
                candidate.LongRunningTaskEnabled = enabled;
                _store.Save(candidate);
                _settings.LongRunningTaskEnabled = enabled;
                Notice = enabled
                    ? "Long Running Task is on. Keeping awake with gentle pointer activity after one idle minute."
                    : "Long Running Task is off. Normal Windows sleep settings apply.";
            }
            catch (Exception)
            {
                // In particular, a failed settings save must never prevent switching
                // the feature off now. The stored choice remains unchanged.
                Notice = $"Long Running Task is {(enabled ? "on" : "off")} for this session, but its preference could not be saved. Reopening will use the previously saved choice.";
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
}
