using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly Dictionary<string, ObservableCollection<ConsoleLine>> _logs = [];
    private readonly Channel<(string ProjectId, string Source, ServiceLog Log)> _pendingLogs =
        Channel.CreateBounded<(string, string, ServiceLog)>(new BoundedChannelOptions(2000)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private readonly DispatcherTimer _consoleTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _forceStopBatchBusy;

    public ObservableCollection<ConsoleLine> ConsoleLines => ProjectConsole(SelectedProject?.Id ?? "launcher");

    private ObservableCollection<ConsoleLine> ProjectConsole(string projectId)
    {
        if (!_logs.TryGetValue(projectId, out var lines)) _logs[projectId] = lines = [];
        return lines;
    }

    private void QueueLog(string projectId, string source, ServiceLog log) =>
        _pendingLogs.Writer.TryWrite((projectId, source, log));

    private void FlushConsoleOutput()
    {
        if (_closeRequested || _closing) return;
        // A busy build must not enqueue one dispatcher operation for every output line.
        // Bound time as well as count so long lines cannot monopolize input/painting.
        var started = Stopwatch.GetTimestamp();
        for (var count = 0; count < 120 && _pendingLogs.Reader.TryRead(out var entry); count++)
        {
            AppendLog(entry.ProjectId, entry.Source, entry.Log);
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 8) break;
        }
    }

    private void AppendLog(string projectId, string source, ServiceLog log)
    {
        var line = ConsoleLine.FromLog(log, source);
        var lines = ProjectConsole(projectId);
        lines.Add(line);
        while (lines.Count > 1000) lines.RemoveAt(0);
        var service = _runners.GetValueOrDefault(projectId)?.FirstOrDefault(item => item.Profile.Id == log.ServiceId);
        if (service is not null)
        {
            service.ConsoleLines.Add(line);
            while (service.ConsoleLines.Count > 600) service.ConsoleLines.RemoveAt(0);
            if (line.Kind is ServiceLogKind.Command or ServiceLogKind.Error)
                RevealServiceConsole(service);
        }
    }

    private static void RevealServiceConsole(ServiceViewModel service)
    {
        // Service activity reveals its own output; the combined console respects the saved layout choice.
        service.IsConsoleVisible = true;
    }

    private void RecordServiceMessage(ServiceViewModel service, string message, ServiceLogKind kind)
    {
        var owner = _runners.FirstOrDefault(item => item.Value.Contains(service)).Key;
        if (owner is not null)
            QueueLog(owner, service.Name, new(DateTime.Now, service.Profile.Id, message, kind == ServiceLogKind.Error, kind));
    }

    private void ClearProjectConsole(object? sender, EventArgs e) => ConsoleLines.Clear();

    private void ClearServiceConsole(object? sender, EventArgs e)
    {
        if (sender is not null && ServiceFrom(sender) is { } service) service.ConsoleLines.Clear();
    }

    private async Task ForceStopAllAsync()
    {
        if (!CanStopBatch) return;
        var targets = Services.Where(service => service.CanForceStop).ToArray();
        _forceStopBatchBusy = true;
        foreach (var service in Services) service.AreCommandsBlocked = true;
        foreach (var service in targets) RevealServiceConsole(service);
        UpdateActions();
        try
        {
            // Normal service trees stop immediately. Conflict dialogs are reviewed one at a time.
            await Task.WhenAll(targets.Where(service => !service.HasConflict).Select(service => StopActionAsync(service)));
            foreach (var service in targets.Where(service => service.HasConflict))
                await StopActionAsync(service, includeConflicts: true);
            var blocked = targets.Count(service => service.Runner.Snapshot.State is not (ServiceState.Stopped or ServiceState.Completed) ||
                service.Runner.HasManagedProcess || service.Runner.Snapshot.ProcessIds.Count > 0);
            Notice = blocked == 0 ? "Force stop finished. Selected services are stopped."
                : $"Force stop finished; {blocked} service(s) still need attention. See their consoles.";
        }
        finally
        {
            foreach (var service in Services) service.AreCommandsBlocked = false;
            _forceStopBatchBusy = false;
            UpdateActions();
        }
    }
}
