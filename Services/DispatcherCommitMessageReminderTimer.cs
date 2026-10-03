using System.Windows.Threading;

namespace FullStackLauncher.Services;

internal sealed class DispatcherCommitMessageReminderTimer : ICommitMessageReminderTimer
{
    private readonly DispatcherTimer _timer;
    private bool _disposed;
    public event EventHandler? Tick;

    public DispatcherCommitMessageReminderTimer(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += ForwardTick;
    }

    public void Start(TimeSpan interval)
    {
        if (_disposed) return;
        _timer.Interval = interval;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
    private void ForwardTick(object? sender, EventArgs e) => Tick?.Invoke(this, e);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= ForwardTick;
        Tick = null;
    }
}
