using FullStackLauncher.Services;

// Silent, deterministic checks: no WPF startup, live settings, audio or real delays.
var checks = new List<(string Name, Action Run)>
{
    ("Sound is opt-in and does not arm while disabled", DisabledDoesNotArm),
    ("Only messages above the exact 3,000-character boundary arm", ExactThreshold),
    ("Leading and trailing whitespace count as message characters", FullUntrimmedMessage),
    ("A missing repository scope does not arm", MissingScopeDoesNotArm),
    ("Enabling waits five minutes before the first reminder", FirstReminderWaits),
    ("Unchanged three-second refreshes do not postpone the reminder", PassiveRefreshKeepsSchedule),
    ("Message growth above the threshold keeps the existing schedule", GrowthKeepsSchedule),
    ("Eligible messages repeat at five-minute intervals", RepeatedReminders),
    ("Duplicate queued ticks cannot cause a notification burst", DuplicateTicksAreSilent),
    ("A late tick produces one reminder and no catch-up burst", LateTickHasNoCatchUp),
    ("Shrinking to the boundary cancels and regrowth waits a full interval", ShrinkAndRegrow),
    ("Disabling cancels and enabling again waits a full interval", DisableAndReenable),
    ("Panel inactivity cancels and resume waits a full interval", InactiveAndResume),
    ("Losing the scope cancels and restoring it waits a full interval", LoseAndRestoreScope),
    ("A stale queued tick is silent after a scope switch", StaleTickAfterScopeSwitch),
    ("Tick-time refresh cancels a now-short message before notifying", RefreshCanShrink),
    ("Tick-time refresh resets a changed repository before notifying", RefreshCanSwitchScope),
    ("Tick-time refresh can disable the reminder before notifying", RefreshCanDisable),
    ("Disposal stops and owns the timer and unsubscribes its handler", DisposalCleansUp),
    ("Disposed reminders ignore later updates and queued ticks", DisposedStateStaysSilent),
};

var scopeChanges = new (string Name, CommitMessageReminderScope Scope)[]
{
    ("project", TestState.Scope with { ProjectId = "project-two" }),
    ("repository root", TestState.Scope with { RepositoryRoot = @"C:\repositories\two" }),
    ("branch", TestState.Scope with { Branch = "feature/two" }),
    ("connection", TestState.Scope with { ConnectionId = "connection-two" }),
    ("remote", TestState.Scope with { RemoteName = "upstream" }),
};
foreach (var change in scopeChanges)
{
    var captured = change;
    checks.Add(($"Changing {captured.Name} starts a fresh five-minute interval",
        () => ScopeSwitchStartsFreshInterval(captured.Scope)));
}

var failures = new List<string>();
foreach (var check in checks)
{
    try
    {
        check.Run();
        Console.WriteLine($"PASS {check.Name}");
    }
    catch (Exception error)
    {
        failures.Add(check.Name);
        Console.Error.WriteLine($"FAIL {check.Name}: {error.Message}");
    }
}
Console.WriteLine($"{checks.Count - failures.Count}/{checks.Count} reminder checks passed.");
return failures.Count == 0 ? 0 : 1;

static void DisabledDoesNotArm()
{
    using var state = new TestState();
    state.Update(enabled: false);
    Check.False(state.Reminder.IsArmed, "Disabled reminders must not be armed.");
    Check.Equal(0, state.Timer.StartCount, "Opt-in is required to start a timer.");
    state.Clock.Advance(TimeSpan.FromHours(1));
    state.Timer.FireQueuedTick();
    Check.Equal(0, state.Notifications, "Disabled reminders must stay silent.");
}

static void ExactThreshold()
{
    using var state = new TestState();
    foreach (var length in new[] { 0, 2_999, 3_000 })
    {
        state.Update(message: new string('x', length));
        Check.False(state.Reminder.IsArmed, $"A {length}-character message must not arm.");
        state.Clock.Advance(CommitMessageReminder.ReminderInterval);
        state.Timer.FireQueuedTick();
        Check.Equal(0, state.Notifications, "Messages at or below the boundary must stay silent.");
    }
    Check.Equal(3_000, CommitMessageReminder.CharacterThreshold, "The threshold is measured in message characters.");
    state.Update(message: new string('x', 3_001));
    Check.True(state.Reminder.IsArmed, "A 3,001-character message must arm.");
    state.AdvanceAndTick(CommitMessageReminder.ReminderInterval);
    Check.Equal(1, state.Notifications, "Above-threshold text must receive a reminder after the interval.");
}

static void FullUntrimmedMessage()
{
    using var state = new TestState();
    state.Update(message: " " + new string('x', 2_999) + "\n");
    Check.True(state.Reminder.IsArmed, "Trimming the message would incorrectly lose characters.");
    state.AdvanceAndTick(CommitMessageReminder.ReminderInterval);
    Check.Equal(1, state.Notifications, "The full untrimmed message is above the threshold.");
    state.Update(message: new string(' ', 3_001));
    Check.True(state.Reminder.IsArmed, "The timer must use actual message length, without a whitespace content filter.");
}

static void MissingScopeDoesNotArm()
{
    using var state = new TestState();
    state.Reminder.Update(null, TestState.LongMessage, enabled: true, active: true);
    Check.False(state.Reminder.IsArmed, "A missing scope must not arm the reminder.");
    state.AdvanceAndTick(TimeSpan.FromHours(1));
    Check.Equal(0, state.Notifications, "A missing scope must remain silent.");
}

static void FirstReminderWaits()
{
    using var state = new TestState();
    state.Update();
    Check.Equal(TimeSpan.FromMinutes(5), state.Timer.LastInterval, "The repeating interval must be five minutes.");
    Check.Equal(0, state.Notifications, "Enabling must not immediately play sound.");
    state.Timer.FireQueuedTick();
    Check.Equal(0, state.Notifications, "An immediate queued tick must be ignored.");
    state.AdvanceAndTick(CommitMessageReminder.ReminderInterval - TimeSpan.FromMilliseconds(1));
    Check.Equal(0, state.Notifications, "A tick before five minutes must be ignored.");
    state.AdvanceAndTick(TimeSpan.FromMilliseconds(1));
    Check.Equal(1, state.Notifications, "Exactly five minutes permits the first reminder.");
}

static void PassiveRefreshKeepsSchedule()
{
    using var state = new TestState();
    state.Update();
    for (var refresh = 0; refresh < 100; refresh++)
    {
        state.Clock.Advance(TimeSpan.FromSeconds(3));
        state.Update();
    }
    Check.Equal(1, state.Timer.StartCount, "Passive polling must not create or restart duplicate timers.");
    state.Timer.FireQueuedTick();
    Check.Equal(1, state.Notifications, "Three-second polling must not postpone the five-minute reminder.");
}

static void GrowthKeepsSchedule()
{
    using var state = new TestState();
    state.Update();
    state.Clock.Advance(TimeSpan.FromMinutes(4));
    state.Update(message: new string('x', 6_000));
    Check.Equal(1, state.Timer.StartCount, "Above-threshold growth must not restart the timer.");
    state.AdvanceAndTick(TimeSpan.FromMinutes(1));
    Check.Equal(1, state.Notifications, "The original five-minute schedule must survive message growth.");
}

static void RepeatedReminders()
{
    using var state = new TestState();
    state.Update();
    for (var count = 1; count <= 3; count++)
    {
        state.AdvanceAndTick(CommitMessageReminder.ReminderInterval);
        Check.Equal(count, state.Notifications, "Eligible messages must repeat every five minutes.");
    }
    Check.True(state.Reminder.IsArmed, "A notification must not disable subsequent reminders.");
}

static void DuplicateTicksAreSilent()
{
    using var state = new TestState();
    state.Update();
    state.AdvanceAndTick(CommitMessageReminder.ReminderInterval);
    for (var tick = 0; tick < 8; tick++) state.Timer.FireQueuedTick();
    Check.Equal(1, state.Notifications, "Queued ticks at the same time must not repeat sound.");
    state.AdvanceAndTick(TimeSpan.FromSeconds(1));
    Check.Equal(1, state.Notifications, "A near-duplicate tick must not repeat sound.");
    state.AdvanceAndTick(CommitMessageReminder.ReminderInterval - TimeSpan.FromSeconds(1));
    Check.Equal(2, state.Notifications, "A later full interval permits another reminder.");
}

static void LateTickHasNoCatchUp()
{
    using var state = new TestState();
    state.Update();
    state.AdvanceAndTick(TimeSpan.FromMinutes(20));
    Check.Equal(1, state.Notifications, "A late timer event sends one reminder only.");
    state.Timer.FireQueuedTick();
    state.AdvanceAndTick(CommitMessageReminder.ReminderInterval - TimeSpan.FromMilliseconds(1));
    Check.Equal(1, state.Notifications, "Missed intervals must not create a catch-up burst.");
    state.AdvanceAndTick(TimeSpan.FromMilliseconds(1));
    Check.Equal(2, state.Notifications, "The next reminder waits five minutes from actual notification.");
}

static void ShrinkAndRegrow()
{
    using var state = new TestState();
    state.Update();
    state.Clock.Advance(TimeSpan.FromMinutes(4));
    state.Update(message: new string('x', 3_000));
    Check.False(state.Reminder.IsArmed, "Shrinking to 3,000 must cancel the timer.");
    Check.False(state.Timer.IsRunning, "No timer should remain active under the boundary.");
    state.AdvanceAndTick(TimeSpan.FromMinutes(1));
    Check.Equal(0, state.Notifications, "A canceled queued tick must remain silent.");
    state.Update();
    Check.Equal(2, state.Timer.StartCount, "Regrowth starts one fresh timer interval.");
    state.AdvanceAndTick(CommitMessageReminder.ReminderInterval - TimeSpan.FromSeconds(1));
    Check.Equal(0, state.Notifications, "Regrowth must not reuse the earlier eligibility time.");
    state.AdvanceAndTick(TimeSpan.FromSeconds(1));
    Check.Equal(1, state.Notifications, "Regrowth permits notification after a fresh interval.");
}

static void DisableAndReenable()
{
    using var state = new TestState();
    state.Update();
    state.Clock.Advance(TimeSpan.FromMinutes(4));
    state.Update(enabled: false);
    Check.False(state.Reminder.IsArmed, "Disabling must disarm immediately.");
    state.AdvanceAndTick(TimeSpan.FromMinutes(1));
    Check.Equal(0, state.Notifications, "Ticks while disabled must stay silent.");
    state.Update(enabled: true);
    state.AdvanceAndTick(TimeSpan.FromMinutes(1));
    Check.Equal(0, state.Notifications, "Re-enabling must start a fresh five-minute wait.");
    state.AdvanceAndTick(TimeSpan.FromMinutes(4));
    Check.Equal(1, state.Notifications, "Re-enabled reminders fire at the new interval.");
}

static void InactiveAndResume()
{
    using var state = new TestState();
    state.Update();
    state.Clock.Advance(TimeSpan.FromMinutes(4));
    state.Update(active: false);
    Check.False(state.Reminder.IsArmed, "An inactive panel/window must disarm.");
    Check.False(state.Timer.IsRunning, "An inactive panel/window must stop its timer.");
    state.AdvanceAndTick(TimeSpan.FromHours(1));
    Check.Equal(0, state.Notifications, "Lifecycle inactivity must remain silent.");
    state.Update(active: true);
    state.AdvanceAndTick(TimeSpan.FromMinutes(1));
    Check.Equal(0, state.Notifications, "Resume waits a new full interval.");
    state.AdvanceAndTick(TimeSpan.FromMinutes(4));
    Check.Equal(1, state.Notifications, "Resume may notify after five eligible minutes.");
}

static void LoseAndRestoreScope()
{
    using var state = new TestState();
    state.Update();
    state.Clock.Advance(TimeSpan.FromMinutes(4));
    state.Reminder.Update(null, TestState.LongMessage, enabled: true, active: true);
    Check.False(state.Reminder.IsArmed, "Losing the current repository must disarm.");
    state.AdvanceAndTick(TimeSpan.FromMinutes(1));
    Check.Equal(0, state.Notifications, "Old-scope queued events stay silent.");
    state.Update();
    state.AdvanceAndTick(TimeSpan.FromMinutes(1));
    Check.Equal(0, state.Notifications, "Restoring a scope must not reuse its prior eligibility.");
    state.AdvanceAndTick(TimeSpan.FromMinutes(4));
    Check.Equal(1, state.Notifications, "The restored scope receives a new five-minute schedule.");
}

static void ScopeSwitchStartsFreshInterval(CommitMessageReminderScope nextScope)
{
    using var state = new TestState();
    state.Update();
    state.Clock.Advance(TimeSpan.FromMinutes(4));
    state.Update(scope: nextScope);
    Check.True(state.Reminder.IsArmed, "An eligible new scope should be armed.");
    Check.Equal(2, state.Timer.StartCount, "A scope switch must start one fresh interval.");
    state.AdvanceAndTick(TimeSpan.FromMinutes(1));
    Check.Equal(0, state.Notifications, "Old-scope elapsed time cannot authorize a new-scope reminder.");
    state.AdvanceAndTick(TimeSpan.FromMinutes(4));
    Check.Equal(1, state.Notifications, "The new scope receives a reminder after its own interval.");
}

static void StaleTickAfterScopeSwitch()
{
    using var state = new TestState();
    state.Update();
    state.Clock.Advance(CommitMessageReminder.ReminderInterval);
    state.Update(scope: TestState.Scope with { RepositoryRoot = @"C:\repositories\two" });
    state.Timer.FireQueuedTick();
    Check.Equal(0, state.Notifications, "A due tick from the previous scope must not notify the new scope.");
    state.AdvanceAndTick(CommitMessageReminder.ReminderInterval);
    Check.Equal(1, state.Notifications, "Only the new scope's full interval can notify.");
}

static void RefreshCanShrink()
{
    using var state = new TestState();
    state.Update();
    var refreshed = 0;
    state.Refresh = () =>
    {
        refreshed++;
        state.Update(message: new string('x', 3_000));
    };
    state.AdvanceAndTick(CommitMessageReminder.ReminderInterval);
    Check.Equal(1, refreshed, "Tick-time state must refresh before notification.");
    Check.Equal(0, state.Notifications, "A current short message cancels a due reminder.");
    Check.False(state.Reminder.IsArmed, "Fresh short state must disarm.");
}

static void RefreshCanSwitchScope()
{
    using var state = new TestState();
    state.Update();
    var nextScope = TestState.Scope with { Branch = "feature/two" };
    state.Refresh = () => state.Update(scope: nextScope);
    state.AdvanceAndTick(CommitMessageReminder.ReminderInterval);
    Check.Equal(0, state.Notifications, "Tick-time scope changes must suppress the old scope's due notification.");
    Check.True(state.Reminder.IsArmed, "An eligible fresh scope remains scheduled.");
    state.AdvanceAndTick(CommitMessageReminder.ReminderInterval);
    Check.Equal(1, state.Notifications, "Unchanged refreshes of the fresh scope preserve its new interval.");
}

static void RefreshCanDisable()
{
    using var state = new TestState();
    state.Update();
    state.Refresh = () => state.Update(enabled: false);
    state.AdvanceAndTick(CommitMessageReminder.ReminderInterval);
    Check.Equal(0, state.Notifications, "The latest disabled state must suppress notification.");
    Check.False(state.Reminder.IsArmed, "The latest disabled state must disarm.");
}

static void DisposalCleansUp()
{
    using var state = new TestState();
    Check.Equal(1, state.Timer.SubscriberCount, "The reminder owns one timer subscription.");
    state.Update();
    state.Reminder.Dispose();
    Check.False(state.Reminder.IsArmed, "Disposed reminders cannot stay armed.");
    Check.False(state.Timer.IsRunning, "Disposal must stop the timer.");
    Check.True(state.Timer.IsDisposed, "Disposal must dispose the owned timer.");
    Check.Equal(0, state.Timer.SubscriberCount, "Disposal must remove the timer subscription.");
    state.Reminder.Dispose();
    Check.Equal(1, state.Timer.DisposeCount, "Disposal is idempotent.");
}

static void DisposedStateStaysSilent()
{
    using var state = new TestState();
    state.Update();
    state.Reminder.Dispose();
    var startsBeforeUpdate = state.Timer.StartCount;
    state.Update();
    state.AdvanceAndTick(TimeSpan.FromHours(1));
    Check.Equal(startsBeforeUpdate, state.Timer.StartCount, "Later updates must not resurrect a disposed timer.");
    Check.Equal(0, state.Notifications, "Disposed queued ticks must remain silent.");
}

sealed class TestState : IDisposable
{
    public static readonly CommitMessageReminderScope Scope =
        new("project-one", @"C:\repositories\one", "feature/one", "connection-one", "origin");
    public static readonly string LongMessage = new('x', 3_001);

    public ManualReminderTimer Timer { get; } = new();
    public ManualTimeProvider Clock { get; } = new();
    public CommitMessageReminder Reminder { get; }
    public int Notifications { get; private set; }
    public Action? Refresh { get; set; }

    public TestState()
    {
        Reminder = new CommitMessageReminder(Timer, () => Notifications++, Clock, () => Refresh?.Invoke());
    }

    public void Update(CommitMessageReminderScope? scope = null, string? message = null,
        bool enabled = true, bool active = true) =>
        Reminder.Update(scope ?? Scope, message ?? LongMessage, enabled, active);

    public void AdvanceAndTick(TimeSpan elapsed)
    {
        Clock.Advance(elapsed);
        Timer.FireQueuedTick();
    }

    public void Dispose() => Reminder.Dispose();
}

sealed class ManualTimeProvider : TimeProvider
{
    private long _timestamp = 100;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;
    public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
}

sealed class ManualReminderTimer : ICommitMessageReminderTimer
{
    private EventHandler? _tick;
    public event EventHandler? Tick
    {
        add => _tick += value;
        remove => _tick -= value;
    }

    public int SubscriberCount => _tick?.GetInvocationList().Length ?? 0;
    public int StartCount { get; private set; }
    public int DisposeCount { get; private set; }
    public TimeSpan LastInterval { get; private set; }
    public bool IsRunning { get; private set; }
    public bool IsDisposed { get; private set; }

    public void Start(TimeSpan interval)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(ManualReminderTimer));
        StartCount++;
        LastInterval = interval;
        IsRunning = true;
    }

    public void Stop() => IsRunning = false;

    // Intentionally allow queued events after Stop, as real dispatchers can do.
    public void FireQueuedTick() => _tick?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        DisposeCount++;
        IsDisposed = true;
        IsRunning = false;
    }
}

static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected {expected}; actual {actual}.");
    }
}
