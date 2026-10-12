using System.Windows;
using System.Windows.Controls;
using FullStackLauncher.Models;
using FullStackLauncher.ProjectTasks;
using FullStackLauncher.Services;

namespace FullStackLauncher.Controls;

public partial class CodexActivityStrip
{
    private readonly CodexAgentKeepAwakeStore _keepAwakeStore = new();
    private readonly HashSet<(string ProjectId, string ThreadId)> _keepAwakeChanging = [];
    private KeepAwakeRead _keepAwakeRead = new(null, new Dictionary<string, CodexAgentKeepAwakeStatus>(), [], null);
    private int _keepAwakeRevision;
    private static readonly CodexAgentKeepAwakeStatus UnownedKeepAwake = new(false, false,
        "Keep awake requires a connected Launcher-started primary agent. Enable it before that agent finishes.");

    private sealed record KeepAwakeRead(string? ProjectId,
        IReadOnlyDictionary<string, CodexAgentKeepAwakeStatus> Cards,
        IReadOnlyList<CodexAgentKeepAwakeEntry> Enabled, string? Error);

    private KeepAwakeRead ReadKeepAwake(CodexActivityFeedSnapshot snapshot, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return new(null, new Dictionary<string, CodexAgentKeepAwakeStatus>(), [], null);
        try
        {
            var statuses = snapshot.ForProject(projectId, "").Agents.ToDictionary(agent => agent.Id,
                agent => _keepAwakeStore.GetStatus(projectId, agent.Id), StringComparer.Ordinal);
            return new(projectId, statuses, _keepAwakeStore.ReadEnabled(projectId), null);
        }
        catch (Exception ex)
        {
            return new(projectId, new Dictionary<string, CodexAgentKeepAwakeStatus>(), [],
                $"Keep awake state could not be read: {SensitiveDataProtection.Redact(ex.Message)}");
        }
    }

    private void ApplyKeepAwakeRead(KeepAwakeRead read) => _keepAwakeRead = read;

    private void ApplyKeepAwakeCards()
    {
        foreach (var card in Agents)
        {
            var status = _keepAwakeRead.ProjectId == _selectedProjectId
                ? _keepAwakeRead.Cards.GetValueOrDefault(card.Id) ??
                    (_keepAwakeRead.Error is { } error ? new(false, false, error) : UnownedKeepAwake)
                : UnownedKeepAwake;
            card.SetKeepAwakeStatus(status, _selectedProjectId is { } projectId && _keepAwakeChanging.Contains((projectId, card.Id)));
        }
    }

    private async void KeepAwake_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ActivityCard card } || !card.CanChangeKeepAwake ||
            string.IsNullOrWhiteSpace(_selectedProjectId)) return;
        await ChangeKeepAwakeAsync(_selectedProjectId, card.Id, !card.KeepAwakeEnabled);
    }

    private async void KeepAwakeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: CodexAgentKeepAwakeEntry entry } || string.IsNullOrWhiteSpace(_selectedProjectId)) return;
        await ChangeKeepAwakeAsync(_selectedProjectId, entry.ThreadId, !entry.Status.IsEnabled);
    }

    private async Task ChangeKeepAwakeAsync(string projectId, string threadId, bool enabled)
    {
        var identity = (projectId, threadId);
        if (!_keepAwakeChanging.Add(identity)) return;
        _keepAwakeRevision++;
        ApplyKeepAwakeCards();
        try
        {
            // This transaction serializes with the owner's final check and complete submission write.
            var status = await Task.Run(() => _keepAwakeStore.SetEnabled(projectId, threadId, enabled));
            if (_selectedProjectId != projectId) return;
            var cards = _keepAwakeRead.ProjectId == projectId
                ? new Dictionary<string, CodexAgentKeepAwakeStatus>(_keepAwakeRead.Cards, StringComparer.Ordinal)
                : new Dictionary<string, CodexAgentKeepAwakeStatus>(StringComparer.Ordinal);
            cards[threadId] = status;
            var entries = _keepAwakeRead.ProjectId == projectId
                ? _keepAwakeRead.Enabled.Where(entry => entry.ThreadId != threadId).ToList() : [];
            if (status.IsEnabled) entries.Add(new(threadId, status));
            _keepAwakeRead = new(projectId, cards, entries, null);
        }
        catch (Exception ex)
        {
            if (IsLoaded)
                MessageBox.Show(Window.GetWindow(this), SensitiveDataProtection.Redact(ex.Message), "Keep awake",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _keepAwakeChanging.Remove(identity);
            _keepAwakeRevision++;
            ApplyKeepAwakeCards();
            if (SettingsMenu.IsOpen) UpdateKeepAwakeMenu();
            await RefreshAsync();
        }
    }

    private void UpdateKeepAwakeMenu()
    {
        KeepAwakeAgentsItem.Items.Clear();
        if (_keepAwakeRead.ProjectId != _selectedProjectId || _keepAwakeRead.Error is not null)
        {
            KeepAwakeAgentsItem.IsEnabled = false;
            KeepAwakeAgentsItem.ToolTip = _keepAwakeRead.Error ?? "Select a project and wait for its Keep awake state.";
            return;
        }
        KeepAwakeAgentsItem.IsEnabled = _keepAwakeRead.Enabled.Count > 0;
        KeepAwakeAgentsItem.ToolTip = "Switch off enabled primary agents, including chats hidden with Wave or Clear. Current turns continue.";
        foreach (var entry in _keepAwakeRead.Enabled)
        {
            var card = Agents.FirstOrDefault(card => card.Id == entry.ThreadId);
            var item = new MenuItem
            {
                Header = card?.Title ?? $"Chat {entry.ThreadId}", Tag = entry,
                IsCheckable = true, IsChecked = entry.Status.IsEnabled, StaysOpenOnClick = true,
                IsEnabled = _selectedProjectId is { } projectId && !_keepAwakeChanging.Contains((projectId, entry.ThreadId)),
                ToolTip = entry.Status.Status, Style = (Style)FindResource("CrewViewMenuItem")
            };
            item.Click += KeepAwakeMenu_Click;
            KeepAwakeAgentsItem.Items.Add(item);
        }
    }
}
