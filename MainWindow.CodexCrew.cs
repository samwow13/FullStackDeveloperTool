using System.Text.Json;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private void UpdateCodexCrewProject()
    {
        var scopes = Projects.Select(project => new CodexActivityProjectScope(project.Id, project.Name,
            new[] { _store.ResolveRoot(project) }.Concat(project.Services.Select(service =>
                _store.ResolveWorkingDirectory(project, service))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
        CodexCrewPanel.SetProjectScope(SelectedProject?.Id, SelectedProject?.Name, scopes);
    }

    public int CodexCrewVisibleAgents
    {
        get => _settings.Layout.CodexCrewVisibleAgents;
        set
        {
            if (value == CodexCrewVisibleAgents) return;
            if (value is not (0 or 1 or 2 or 3 or 4 or 6))
            {
                RestoreCodexCrewPreference(nameof(CodexCrewVisibleAgents));
                return;
            }

            SaveCodexCrewPreference(nameof(CodexCrewVisibleAgents), "view", layout => layout.CodexCrewVisibleAgents = value);
        }
    }

    public bool CodexCrewThoughtBubblesEnabled
    {
        get => _settings.Layout.CodexCrewThoughtBubblesEnabled;
        set
        {
            if (value == CodexCrewThoughtBubblesEnabled) return;
            SaveCodexCrewPreference(nameof(CodexCrewThoughtBubblesEnabled), "thought bubble choice",
                layout => layout.CodexCrewThoughtBubblesEnabled = value);
        }
    }

    public int CodexCrewThoughtBubbleSeconds
    {
        get => _settings.Layout.CodexCrewThoughtBubbleSeconds;
        set
        {
            if (value == CodexCrewThoughtBubbleSeconds) return;
            if (value is < 3 or > 120)
            {
                RestoreCodexCrewPreference(nameof(CodexCrewThoughtBubbleSeconds));
                return;
            }

            SaveCodexCrewPreference(nameof(CodexCrewThoughtBubbleSeconds), "thought bubble duration",
                layout => layout.CodexCrewThoughtBubbleSeconds = value);
        }
    }

    private void SaveCodexCrewPreference(string propertyName, string preferenceName, Action<WorkspaceLayout> update)
    {
        if (!_layoutReady || _closed || _closing || _closeRequested)
        {
            RestoreCodexCrewPreference(propertyName);
            return;
        }

        try
        {
            // Clone at save time so every other profile and layout preference is retained.
            var candidate = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(_settings))!;
            update(candidate.Layout);
            _store.Save(candidate);
            update(_settings.Layout);
            Changed(propertyName);
        }
        catch (Exception ex)
        {
            Notice = $"Codex crew {preferenceName} could not be saved. The previous choice is kept. {ex.Message}";
            RestoreCodexCrewPreference(propertyName);
        }
    }

    private void RestoreCodexCrewPreference(string propertyName)
    {
        if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        // A notification inside a TwoWay source setter can be ignored during its current transfer.
        // Refresh after that transfer finishes so the control restores the saved value.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            if (!_closed) Changed(propertyName);
        }));
    }
}
