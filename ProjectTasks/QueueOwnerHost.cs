using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FullStackLauncher.Services;
using FullStackLauncher.CodexMonitor;
using Forms = System.Windows.Forms;

namespace FullStackLauncher.ProjectTasks;

/// <summary>
/// One independent tray owner per task store. The owner never starts from a
/// saved queue alone: the coordinator first persists crash recovery, then
/// accepts explicit commands and dispatches only queues already enabled.
/// </summary>
internal sealed class QueueOwnerHost : IDisposable
{
    private const uint Continuous = 0x80000000;
    private const uint SystemRequired = 0x00000001;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ProjectTaskStore _store = new();
    private readonly SettingsStore _settings = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly ProjectQueueCoordinator _coordinator;
    private Mutex? _instance;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _pauseAllMenu;
    private Forms.ToolStripMenuItem? _resumeAllMenu;
    private Forms.ToolStripMenuItem? _stopCurrentMenu;
    private Task? _coordinatorTask;
    private Task? _pipeTask;
    private bool _awake;
    private bool _awakeFailed;
    private bool _polling;
    private bool _disposed;
    private string? _activeProjectId;
    private string? _activeAttemptId;

    internal QueueOwnerHost()
    {
        _coordinator = new ProjectQueueCoordinator(_store, _settings, () => new CodexQueueTaskRunner());
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
    }

    internal static async void Open(string[] args)
    {
        try
        {
            if (MonitorRuntime.PrepareStart(args) is { } start)
            {
                using var process = Process.Start(start)
                    ?? throw new IOException("The independent queue owner could not start.");
                Application.Current.Shutdown();
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            try { Console.Error.WriteLine("The queue owner runtime could not be prepared."); }
            catch { }
            Application.Current.Shutdown(1);
            return;
        }
        using var host = new QueueOwnerHost();
        try { await host.RunAsync(); }
        catch (Exception ex)
        {
            // This hidden process has no dashboard to report through. Avoid leaking
            // task content or settings paths in a crash dialog.
            try { Console.Error.WriteLine("The queue owner stopped: " + ex.GetType().Name); }
            catch { }
        }
        finally { Application.Current.Shutdown(); }
    }

    private async Task RunAsync()
    {
        var instance = new Mutex(false, QueueOwnerClient.MutexNameFor(_store.StorePath));
        bool acquired;
        try { acquired = instance.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            instance.Dispose();
            return;
        }
        _instance = instance;

        // RunAsync owns recovery and serial dispatch. Pipe requests must wait for
        // Ready so no command can be applied against an unrecovered store.
        _coordinatorTask = Task.Run(() => _coordinator.RunAsync(_lifetime.Token));
        var first = await Task.WhenAny(_coordinator.Ready, _coordinatorTask);
        if (first == _coordinatorTask) await _coordinatorTask;
        await _coordinator.Ready;
        CreateTray();
        _pipeTask = Task.Run(() => ServePipeAsync(_lifetime.Token));
        _statusTimer.Start();
        await RefreshStatusAsync();
        await Task.WhenAny(_coordinatorTask, _pipeTask);
        _lifetime.Cancel();
        _statusTimer.Stop();
        try { await Task.WhenAll(_coordinatorTask, _pipeTask); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void CreateTray()
    {
        using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/launcher.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(resource),
            Text = "Full Stack Launcher queue",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _pauseAllMenu = new Forms.ToolStripMenuItem("Pause all queues", null,
            async (_, _) => await ApplyTrayCommandAsync("pause-all", paused: true));
        _resumeAllMenu = new Forms.ToolStripMenuItem("Clear global pause", null,
            async (_, _) => await ApplyTrayCommandAsync("pause-all", paused: false));
        _stopCurrentMenu = new Forms.ToolStripMenuItem("Stop current task", null,
            async (_, _) => await ApplyTrayCommandAsync("stop", _activeProjectId, attemptId: _activeAttemptId));
        _tray.ContextMenuStrip.Items.Add(_pauseAllMenu);
        _tray.ContextMenuStrip.Items.Add(_resumeAllMenu);
        _tray.ContextMenuStrip.Items.Add(_stopCurrentMenu);
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Exit queue owner", null, async (_, _) =>
        {
            try
            {
                var status = await _coordinator.GetStatusAsync(null);
                if (status.HasActiveWork)
                {
                    ShowBalloon("Queue owner", "Pause all queues or stop the current task before exiting the queue owner.", Forms.ToolTipIcon.Warning);
                    return;
                }
                _lifetime.Cancel();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
            {
                ShowBalloon("Queue owner", "Queue status is unavailable. Exit was not applied.", Forms.ToolTipIcon.Warning);
            }
        });
    }

    private async Task ApplyTrayCommandAsync(string command, string? projectId = null,
        bool? paused = null, string? attemptId = null)
    {
        try
        {
            var reply = await ApplyCommandAsync(new QueueOwnerRequest(QueueOwnerClient.ProtocolVersion,
                command, projectId, paused, attemptId), _lifetime.Token);
            ShowBalloon("Queue owner", reply.Message, reply.Success ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task ServePipeAsync(CancellationToken cancellationToken)
    {
        var name = QueueOwnerClient.PipeNameFor(_store.StorePath);
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken);
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
                    { AutoFlush = true };
                var line = await ReadBoundedLineAsync(reader, 4096, cancellationToken);
                QueueOwnerReply reply;
                try
                {
                    var request = JsonSerializer.Deserialize<QueueOwnerRequest>(line, JsonOptions)
                        ?? throw new ArgumentException("The queue command is empty.");
                    reply = await ApplyCommandAsync(request, cancellationToken);
                }
                catch (Exception ex) when (ex is ArgumentException or JsonException)
                {
                    reply = new(false, "The queue command was invalid.");
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ArgumentException)
            {
                if (cancellationToken.IsCancellationRequested) break;
                // A disconnected client never changes queue state or stops owner.
            }
        }
    }

    private async Task<QueueOwnerReply> ApplyCommandAsync(QueueOwnerRequest request, CancellationToken cancellationToken)
    {
        if (request.Version != QueueOwnerClient.ProtocolVersion)
            return new(false, "The queue control protocol changed. Restart the launcher and queue owner.");
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            switch (request.Command)
            {
                case "enable" when !string.IsNullOrWhiteSpace(request.ProjectId):
                    await _coordinator.EnableQueueAsync(request.ProjectId, cancellationToken);
                    break;
                case "pause" when !string.IsNullOrWhiteSpace(request.ProjectId):
                    await _coordinator.PauseQueueAsync(request.ProjectId, cancellationToken);
                    break;
                case "pause-all" when request.Paused.HasValue:
                    await _coordinator.PauseAllAsync(request.Paused.Value, cancellationToken);
                    break;
                case "stop" when !string.IsNullOrWhiteSpace(request.ProjectId) &&
                                 !string.IsNullOrWhiteSpace(request.AttemptId):
                    await _coordinator.StopCurrentAsync(request.ProjectId, request.AttemptId,
                        cancellationToken);
                    break;
                case "delete-attempt" when !string.IsNullOrWhiteSpace(request.ProjectId) &&
                                           !string.IsNullOrWhiteSpace(request.AttemptId):
                    await _coordinator.AbandonAttemptAsync(request.ProjectId, request.AttemptId,
                        cancellationToken);
                    break;
                case "review-release" when !string.IsNullOrWhiteSpace(request.ProjectId) &&
                                           !string.IsNullOrWhiteSpace(request.AttemptId):
                    await _coordinator.ReleaseAttemptAfterReviewAsync(request.ProjectId,
                        request.AttemptId, request.ConfirmedNoActiveTask, cancellationToken);
                    break;
                case "dismiss-unsubmitted" when !string.IsNullOrWhiteSpace(request.ProjectId) &&
                                                 !string.IsNullOrWhiteSpace(request.AttemptId):
                    await _coordinator.DismissUnsubmittedAttemptAsync(request.ProjectId,
                        request.AttemptId, request.ConfirmedNoActiveTask, cancellationToken);
                    break;
                case "retry-item" when !string.IsNullOrWhiteSpace(request.ProjectId) &&
                                       !string.IsNullOrWhiteSpace(request.ItemId) &&
                                       !string.IsNullOrWhiteSpace(request.AttemptId):
                    await _coordinator.RetryItemAsync(request.ProjectId, request.ItemId,
                        request.AttemptId, request.ConfirmedRetry, cancellationToken);
                    break;
                case "status":
                    break;
                default:
                    return new(false, "The queue command is invalid.");
            }
            var status = await _coordinator.GetStatusAsync(request.ProjectId);
            return new(true, AppendPowerWarning(status.Message), null,
                status.HasActiveWork, status.CanStopCurrent, status.ActiveProjectId,
                status.ActiveAttemptId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new(false, ex.Message);
        }
        finally { _commandGate.Release(); }
    }

    private async Task RefreshStatusAsync()
    {
        if (_polling || _disposed) return;
        _polling = true;
        try
        {
            var status = await _coordinator.GetStatusAsync(null);
            _activeProjectId = status.ActiveProjectId;
            _activeAttemptId = status.ActiveAttemptId;
            SetAwake(status.HasActiveWork);
            if (_tray != null)
            {
                _tray.Text = status.HasActiveWork ? "Full Stack Launcher queue — active" : "Full Stack Launcher queue";
                if (_stopCurrentMenu != null) _stopCurrentMenu.Enabled = status.CanStopCurrent;
            }
            // Claim before popup. A crash or Windows suppression cannot create a duplicate.
            var notification = await _coordinator.ClaimNextPendingNotificationAsync(_lifetime.Token);
            if (notification != null)
                ShowBalloon("Queue task update", notification.Message, Forms.ToolTipIcon.Info);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            // Coordinator reports durable errors through status; never infer success.
            // Keep an existing power request during an uncertain status read.
        }
        finally { _polling = false; }
    }

    private string AppendPowerWarning(string message) => _awakeFailed
        ? message + " Windows keep-awake request failed; normal idle sleep settings apply."
        : message;

    private void SetAwake(bool needed)
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (needed == _awake)
        {
            if (!needed) _awakeFailed = false;
            return;
        }
        if (SetThreadExecutionState(Continuous | (needed ? SystemRequired : 0)) == 0)
        {
            var wasFailed = _awakeFailed;
            _awakeFailed = needed;
            if (needed && !wasFailed) ShowBalloon("Queue keep-awake unavailable",
                "Windows did not accept the queue's system-awake request. Normal idle sleep settings apply.",
                Forms.ToolTipIcon.Warning);
            return;
        }
        _awake = needed;
        _awakeFailed = false;
    }

    private void ShowBalloon(string title, string message, Forms.ToolTipIcon icon)
    {
        try { _tray?.ShowBalloonTip(10000, title[..Math.Min(title.Length, 63)],
            message[..Math.Min(message.Length, 250)], icon); }
        catch (Exception) { /* Receipt records attempted delivery even if Windows suppresses it. */ }
    }

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, int maximum,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1];
        var result = new StringBuilder();
        while (result.Length < maximum)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0) throw new IOException("The queue client disconnected.");
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            result.Append(buffer[0]);
        }
        throw new ArgumentException("The queue command is too large.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _statusTimer.Stop();
        if (_awake) SetThreadExecutionState(Continuous);
        _tray?.Dispose();
        _commandGate.Dispose();
        _lifetime.Dispose();
        if (_instance != null)
        {
            _instance.ReleaseMutex();
            _instance.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);
}
