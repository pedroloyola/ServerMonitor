namespace ServerMonitor.Core.Tests.Backup;

/// <summary>
/// A hand-written manual clock (no test package, Vigil C-13): time moves only when the test calls
/// <see cref="Advance"/>, which fires every timer that has come due. <see cref="TimerCreated"/> completes once the
/// code under test has armed its FIRST timer (and stays completed), so a test can wait for "the drain is now
/// waiting" without any wall-clock sleep, whether it asks before or after that happened.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly TaskCompletionSource _timerCreated = NewSignal();

    public Task TimerCreated => _timerCreated.Task;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_sync)
        {
            _timers.Add(timer);
            timer.Arm(_now, dueTime);
        }

        _timerCreated.TrySetResult();
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_sync)
        {
            _now += by;
            due = _timers.Where(timer => timer.DueAt is { } at && at <= _now).ToList();
            foreach (var timer in due)
            {
                timer.DueAt = null;
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public void Arm(DateTimeOffset now, TimeSpan dueTime) =>
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                Arm(owner._now, dueTime);
            }

            return true;
        }

        public void Dispose()
        {
            lock (owner._sync)
            {
                DueAt = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
