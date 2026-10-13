using System.Windows.Threading;

namespace FullStackLauncher.Services;

/// <summary>Daily Pacific-time cutoffs for the dashboard's keep-awake switch.</summary>
internal sealed class LongRunningTaskAutoOffSchedule : IDisposable
{
    private static readonly TimeZoneInfo PacificTime = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
    private static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan[] WindowStarts = [new(11, 45, 0), new(16, 0, 0)];
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _turnOff;
    private DateOnly _scheduledDate;
    private DateTimeOffset[] _cutoffs = [];
    private int _nextCutoff;
    private bool _disposed;

    internal LongRunningTaskAutoOffSchedule(Dispatcher dispatcher, Func<bool> turnOff)
    {
        _dispatcher = dispatcher;
        _dispatcher.VerifyAccess();
        _turnOff = turnOff;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _timer.Tick += Tick;
        var now = DateTimeOffset.UtcNow;
        CreateSchedule(now, initialStartup: true);
        SetNextInterval(now);
        _timer.Start();
    }

    private void CreateSchedule(DateTimeOffset now, bool initialStartup)
    {
        var pacificNow = TimeZoneInfo.ConvertTime(now, PacificTime);
        _scheduledDate = DateOnly.FromDateTime(pacificNow.DateTime);
        var cutoffs = new List<DateTimeOffset>(WindowStarts.Length);
        foreach (var start in WindowStarts)
        {
            var localStart = _scheduledDate.ToDateTime(TimeOnly.FromTimeSpan(start));
            var windowStart = new DateTimeOffset(localStart, PacificTime.GetUtcOffset(localStart));
            var windowEnd = windowStart + WindowLength;
            // A new dashboard skips elapsed windows. Starting inside a window
            // still picks a cutoff within its remaining time, rather than missing it.
            if (initialStartup && now >= windowEnd) continue;
            var earliest = initialStartup && now > windowStart ? now : windowStart;
            var offset = Random.Shared.NextInt64((windowEnd - earliest).Ticks);
            cutoffs.Add(earliest + TimeSpan.FromTicks(offset));
        }
        _cutoffs = cutoffs.ToArray();
        _nextCutoff = 0;
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var now = DateTimeOffset.UtcNow;
        // Handle an overdue cutoff before advancing the date after resume.
        // Failed native releases remain due and retry on the next timer tick.
        if (!ProcessDueCutoffs(now))
        {
            _timer.Interval = TimeSpan.FromSeconds(10);
            return;
        }
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, PacificTime).DateTime);
        // Clock rollback must not regenerate cutoffs already consumed.
        if (date > _scheduledDate)
        {
            CreateSchedule(now, initialStartup: false);
            if (!ProcessDueCutoffs(now))
            {
                _timer.Interval = TimeSpan.FromSeconds(10);
                return;
            }
        }
        SetNextInterval(now);
    }

    private bool ProcessDueCutoffs(DateTimeOffset now)
    {
        while (_nextCutoff < _cutoffs.Length && now >= _cutoffs[_nextCutoff])
        {
            if (!_turnOff()) return false;
            _nextCutoff++;
        }
        return true;
    }

    private void SetNextInterval(DateTimeOffset now)
    {
        var delay = _nextCutoff < _cutoffs.Length
            ? _cutoffs[_nextCutoff] - now
            : TimeSpan.FromSeconds(30);
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 1, 30_000));
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Tick;
    }
}
