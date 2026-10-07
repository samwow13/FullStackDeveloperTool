using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;
using FullStackLauncher.ViewModels;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly Channel<(string ProjectId, ServiceRunner Runner, string Source, ServiceLog Log)> _pendingLogs =
        Channel.CreateBounded<(string, ServiceRunner, string, ServiceLog)>(new BoundedChannelOptions(2000)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private readonly ConditionalWeakTable<ServiceRunner, ConsoleResetState> _consoleResetStates = new();
    private readonly DispatcherTimer _consoleTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _copyErrorsFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Popup? _copyErrorsFeedback;
    private bool _forceStopBatchBusy;

    private sealed class ConsoleResetState
    {
        public long AppliedSequence { get; set; }
    }

    private void QueueLog(string projectId, ServiceRunner runner, string source, ServiceLog log) =>
        _pendingLogs.Writer.TryWrite((projectId, runner, source, log));

    private ServiceViewModel? FindConsoleService(string projectId, ServiceRunner runner) =>
        _runners.GetValueOrDefault(projectId)?.FirstOrDefault(item => ReferenceEquals(item.Runner, runner));

    private void QueueConsoleReset(string projectId, ServiceRunner runner)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (FindConsoleService(projectId, runner) is { } service) ApplyConsoleReset(service);
        }), DispatcherPriority.Background);
    }

    private long ApplyConsoleReset(ServiceViewModel service)
    {
        var sequence = service.Runner.ConsoleResetSequence;
        var state = _consoleResetStates.GetValue(service.Runner, static _ => new ConsoleResetState());
        if (state.AppliedSequence != sequence)
        {
            service.ClearConsoleLines();
            state.AppliedSequence = sequence;
        }
        return sequence;
    }

    private void FlushConsoleOutput()
    {
        if (_closeRequested || _closing) return;
        // A busy build must not enqueue one dispatcher operation for every output line.
        // Bound time as well as count so long lines cannot monopolize input/painting.
        var started = Stopwatch.GetTimestamp();
        for (var count = 0; count < 120 && _pendingLogs.Reader.TryRead(out var entry); count++)
        {
            AppendLog(entry.ProjectId, entry.Runner, entry.Source, entry.Log);
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 8) break;
        }
    }

    private void AppendLog(string projectId, ServiceRunner runner, string source, ServiceLog log)
    {
        if (FindConsoleService(projectId, runner) is not { } service) return;
        if (log.ConsoleSequence <= ApplyConsoleReset(service)) return;
        service.AppendConsoleLine(ConsoleLine.FromLog(log, source));
    }

    private void RecordServiceMessage(ServiceViewModel service, string message, ServiceLogKind kind) =>
        service.Runner.RecordConsoleMessage(message, kind);

    private void ClearServiceConsole(object? sender, EventArgs e)
    {
        if (sender is null || ServiceFrom(sender) is not { } service) return;
        ClearServiceOutput(service);
    }

    private void ClearServiceOutput(ServiceViewModel service)
    {
        service.Runner.ClearConsoleOutput();
        ApplyConsoleReset(service);
        Notice = $"Cleared {service.Name} output. New output will continue to appear.";
    }

    private void CopyServiceConsole_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceFrom(sender) is not { } service) return;
        var lines = service.ConsoleLines.ToArray();
        if (lines.Length == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines.Select(line => line.PlainText)));
            Notice = $"Copied {lines.Length:N0} {(lines.Length == 1 ? "line" : "lines")} of {service.Name} output.";
        }
        catch (ExternalException)
        {
            Notice = "Clipboard is busy. Try Copy output again.";
        }
    }

    private void CopyServiceErrors_Click(object sender, RoutedEventArgs e)
    {
        CloseCopyErrorsFeedback();
        if (ServiceFrom(sender) is not { } service) return;
        CopyServiceErrorOutput(service, sender as FrameworkElement);
    }

    private void CopyServiceStatusError_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseCopyErrorsFeedback();
        if (ServiceFrom(sender) is not { } service) return;
        service.Update();
        if (!service.HasCopyableStatusError) return;
        CopyServiceErrorOutput(service, sender as FrameworkElement, service.CardStatusDetail);
    }

    private void CopyServiceErrorOutput(ServiceViewModel service, FrameworkElement? target, string? currentError = null)
    {
        var lines = service.ConsoleLines.ToArray();
        var included = new bool[lines.Length];
        var errorCount = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Kind != ServiceLogKind.Error) continue;
            errorCount++;
            // Union the preceding context windows in capture order without duplicating entries.
            for (var context = Math.Max(0, index - 10); context <= index; context++)
                included[context] = true;
        }
        if (errorCount == 0 && currentError is null)
        {
            Notice = $"No retained errors to copy for {service.Name}.";
            return;
        }
        try
        {
            var copiedLines = lines.Where((_, index) => included[index]).ToArray();
            var text = string.Join(Environment.NewLine, copiedLines.Select(line => line.PlainText));
            if (currentError is not null && !copiedLines.Any(line => line.Message == currentError))
                text = (text.Length == 0 ? "" : text + Environment.NewLine) + $"[ERROR] [{service.Name}] {currentError}";
            Clipboard.SetText(text);
            Notice = currentError is not null ? $"Copied current error from {service.Name}."
                : $"Copied {errorCount:N0} {(errorCount == 1 ? "error" : "errors")} from {service.Name} with up to 10 preceding console entries per error.";
            if (target is not null) ShowCopyErrorsFeedback(target);
        }
        catch (ExternalException)
        {
            Notice = "Clipboard is busy. Try Copy Errors again.";
        }
    }

    private void ShowCopyErrorsFeedback(FrameworkElement target)
    {
        _copyErrorsFeedback = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            AllowsTransparency = true,
            StaysOpen = false,
            Child = new Border
            {
                Background = (Brush)FindResource("CardBrush"),
                BorderBrush = (Brush)FindResource("AccentBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Child = new TextBlock
                {
                    Text = "Copied Error",
                    Foreground = (Brush)FindResource("AccentBrush"),
                    FontSize = 12
                }
            },
            IsOpen = true
        };
        _copyErrorsFeedbackTimer.Start();
    }

    private void CloseCopyErrorsFeedback()
    {
        _copyErrorsFeedbackTimer.Stop();
        if (_copyErrorsFeedback is { } popup) popup.IsOpen = false;
        _copyErrorsFeedback = null;
    }

    private async Task ForceStopAllAsync()
    {
        if (!CanStopBatch) return;
        var targets = Services.Where(service => service.CanForceStop).ToArray();
        var projectId = SelectedProject?.Id;
        if (projectId is not null)
            RecordAgentBridgeProjectEvent(projectId, "force_stop_all", "requested", revokeReservations: true);
        _forceStopBatchBusy = true;
        foreach (var service in Services) service.AreCommandsBlocked = true;
        UpdateActions();
        try
        {
            // Normal service trees stop immediately. Conflict dialogs are reviewed one at a time.
            await Task.WhenAll(targets.Where(service => !service.HasConflict).Select(service => StopActionAsync(service)));
            foreach (var service in targets.Where(service => service.HasConflict))
                await StopActionAsync(service, includeConflicts: true);
            var blocked = targets.Count(service => service.Runner.Snapshot.State is not (ServiceState.Stopped or ServiceState.Completed) ||
                service.Runner.HasManagedProcess || service.Runner.Snapshot.ProcessIds.Count > 0);
            if (projectId is not null)
                RecordAgentBridgeProjectEvent(projectId, "force_stop_all", blocked == 0 ? "completed" : "failed");
            Notice = blocked == 0 ? "Force stop finished. Selected services are stopped."
                : $"Force stop finished; {blocked} service(s) still need attention. See their consoles.";
        }
        catch
        {
            if (projectId is not null) RecordAgentBridgeProjectEvent(projectId, "force_stop_all", "failed");
            throw;
        }
        finally
        {
            foreach (var service in Services) service.AreCommandsBlocked = false;
            _forceStopBatchBusy = false;
            UpdateActions();
        }
    }
}
