using System.Text.Json;
using System.Windows.Threading;
using FullStackLauncher.Models;

namespace FullStackLauncher;

public partial class MainWindow
{
    public int CodexCrewVisibleAgents
    {
        get => _settings.Layout.CodexCrewVisibleAgents;
        set
        {
            if (value == CodexCrewVisibleAgents) return;
            if (value is not (0 or 1 or 2 or 3 or 4 or 6) ||
                !_layoutReady || _closed || _closing || _closeRequested)
            {
                RestoreCodexCrewViewPreference();
                return;
            }

            try
            {
                // Clone at save time so every other profile and layout preference is retained.
                var candidate = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(_settings))!;
                candidate.Layout.CodexCrewVisibleAgents = value;
                _store.Save(candidate);
                _settings.Layout.CodexCrewVisibleAgents = candidate.Layout.CodexCrewVisibleAgents;
                Changed(nameof(CodexCrewVisibleAgents));
            }
            catch (Exception ex)
            {
                Notice = $"Codex crew view could not be saved. The previous choice is kept. {ex.Message}";
                RestoreCodexCrewViewPreference();
            }
        }
    }

    private void RestoreCodexCrewViewPreference()
    {
        if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        // A notification inside a TwoWay source setter can be ignored during its current transfer.
        // Refresh after that transfer finishes so the selector and cards restore the saved value.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            if (!_closed) Changed(nameof(CodexCrewVisibleAgents));
        }));
    }
}
