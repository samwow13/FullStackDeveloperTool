using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private CodexAccountUsageService _codexAccountUsageService = new();
    private CancellationTokenSource _codexAccountUsageLifetime = new();
    private readonly DispatcherTimer _codexAccountUsageTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _codexAccountUsageRefreshing;
    private bool _codexAccountUsageStopped;
    private Task? _codexAccountUsageStop;
    public CodexAccountUsageSnapshot? CodexAccountUsage { get; private set; }

    private void InitializeCodexAccountUsage()
    {
        _codexAccountUsageTimer.Tick += async (_, _) => await RefreshCodexAccountUsageAsync();
        Loaded += async (_, _) =>
        {
            if (_codexAccountUsageStopped || _closed || _closing || _closeRequested) return;
            _codexAccountUsageTimer.Start();
            await RefreshCodexAccountUsageAsync();
        };
        Activated += async (_, _) =>
        {
            if (IsLoaded) await RefreshCodexAccountUsageAsync();
        };
        Closed += (_, _) => _ = StopCodexAccountUsageAsync();
    }

    private Task StopCodexAccountUsageAsync()
    {
        Dispatcher.VerifyAccess();
        return _codexAccountUsageStop ??= StopCoreAsync(_codexAccountUsageService, _codexAccountUsageLifetime);
    }

    private async Task StopCoreAsync(CodexAccountUsageService reader, CancellationTokenSource lifetime)
    {
        // Block refresh before cancellation can yield to another activation event.
        _codexAccountUsageStopped = true;
        _codexAccountUsageTimer.Stop();
        try
        {
            lifetime.Cancel();
            await reader.DisposeAsync();
        }
        finally { lifetime.Dispose(); }
    }

    private async Task RestartCodexAccountUsageAfterCanceledCloseAsync()
    {
        Dispatcher.VerifyAccess();
        var stopped = _codexAccountUsageStop;
        if (stopped is null) return;
        // Recreate only after the previous owned reader confirms cleanup. A failed
        // cleanup keeps refresh stopped rather than opening a second reader.
        await stopped;
        if (_closed || _closing || _closeRequested || !ReferenceEquals(stopped, _codexAccountUsageStop)) return;
        _codexAccountUsageService = new();
        _codexAccountUsageLifetime = new();
        _codexAccountUsageStop = null;
        _codexAccountUsageStopped = false;
        _codexAccountUsageRefreshing = false;
        if (CodexAccountUsage is { } usage)
        {
            CodexAccountUsage = usage with { IsStale = usage.LastUpdatedAt is not null };
            Changed(nameof(CodexAccountUsage));
        }
        // Existing handlers observe the replacement fields; register none again.
        if (IsLoaded) _codexAccountUsageTimer.Start();
    }

    private async Task RefreshCodexAccountUsageAsync()
    {
        Dispatcher.VerifyAccess();
        if (_codexAccountUsageRefreshing || _codexAccountUsageStopped || _codexAccountUsageStop is not null ||
            _closed || _closing || _closeRequested) return;
        var reader = _codexAccountUsageService;
        var lifetime = _codexAccountUsageLifetime;
        _codexAccountUsageRefreshing = true;
        var cancellationToken = CancellationToken.None;
        try
        {
            cancellationToken = lifetime.Token;
            // The service performs executable discovery and account I/O off the dispatcher.
            var snapshot = await reader.ReadAsync(cancellationToken);
            if (_closed || _closing || _closeRequested || _codexAccountUsageStopped ||
                cancellationToken.IsCancellationRequested || !ReferenceEquals(reader, _codexAccountUsageService) ||
                !ReferenceEquals(lifetime, _codexAccountUsageLifetime)) return;
            CodexAccountUsage = snapshot;
            Changed(nameof(CodexAccountUsage));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_codexAccountUsageStopped || cancellationToken.IsCancellationRequested ||
            !ReferenceEquals(lifetime, _codexAccountUsageLifetime)) { }
        finally
        {
            // An old read can finish after restart. It must not unlock a new generation's read.
            if (ReferenceEquals(reader, _codexAccountUsageService) && ReferenceEquals(lifetime, _codexAccountUsageLifetime))
                _codexAccountUsageRefreshing = false;
        }
    }
}
