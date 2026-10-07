using System.Windows.Threading;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private readonly CodexAccountUsageService _codexAccountUsageService = new();
    private readonly CancellationTokenSource _codexAccountUsageLifetime = new();
    private readonly DispatcherTimer _codexAccountUsageTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _codexAccountUsageRefreshing;
    private Task? _codexAccountUsageStop;
    public CodexAccountUsageSnapshot? CodexAccountUsage { get; private set; }

    private void InitializeCodexAccountUsage()
    {
        _codexAccountUsageTimer.Tick += async (_, _) => await RefreshCodexAccountUsageAsync();
        Loaded += async (_, _) =>
        {
            _codexAccountUsageTimer.Start();
            await RefreshCodexAccountUsageAsync();
        };
        Activated += async (_, _) =>
        {
            if (IsLoaded) await RefreshCodexAccountUsageAsync();
        };
        Closed += (_, _) => _ = StopCodexAccountUsageAsync();
    }

    private Task StopCodexAccountUsageAsync() => _codexAccountUsageStop ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        _codexAccountUsageTimer.Stop();
        _codexAccountUsageLifetime.Cancel();
        await _codexAccountUsageService.DisposeAsync();
        _codexAccountUsageLifetime.Dispose();
    }

    private async Task RefreshCodexAccountUsageAsync()
    {
        if (_codexAccountUsageRefreshing || _closed || _closing || _closeRequested) return;
        _codexAccountUsageRefreshing = true;
        var cancellationToken = _codexAccountUsageLifetime.Token;
        try
        {
            // The service performs executable discovery and account I/O off the dispatcher.
            var snapshot = await _codexAccountUsageService.ReadAsync(cancellationToken);
            if (_closed || cancellationToken.IsCancellationRequested) return;
            CodexAccountUsage = snapshot;
            Changed(nameof(CodexAccountUsage));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { _codexAccountUsageRefreshing = false; }
    }
}
