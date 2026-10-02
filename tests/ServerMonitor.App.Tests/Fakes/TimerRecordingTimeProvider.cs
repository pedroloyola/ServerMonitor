using Microsoft.Extensions.Time.Testing;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that reports when a timer with a given due time has been registered, so a test
/// can advance the clock exactly once AFTER the code under test is parked on that timer (instead of advancing
/// repeatedly until something happens, or advancing inside the window between reading the clock and arming the timer,
/// which would arm the timer relative to the ALREADY-advanced clock). The signal is raised only after the base
/// provider has registered the timer, so an advance made on it always reaches that timer.
/// </summary>
internal sealed class TimerRecordingTimeProvider : FakeTimeProvider
{
    private readonly object _sync = new();
    private readonly List<TimeSpan> _created = [];
    private readonly List<(TimeSpan DueTime, int Occurrence, TaskCompletionSource Signal)> _waiters = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period); // registered before anyone is told
        lock (_sync)
        {
            _created.Add(dueTime);
            var count = CountLocked(dueTime);
            foreach (var waiter in _waiters.Where(waiter => waiter.DueTime == dueTime && count >= waiter.Occurrence).ToArray())
            {
                waiter.Signal.TrySetResult();
                _waiters.Remove(waiter);
            }
        }

        return timer;
    }

    /// <summary>How many timers with <paramref name="dueTime"/> have been created so far.</summary>
    public int CreatedCount(TimeSpan dueTime)
    {
        lock (_sync)
        {
            return CountLocked(dueTime);
        }
    }

    /// <summary>
    /// Completes once the <paramref name="occurrence"/>-th timer with <paramref name="dueTime"/> has been created
    /// (counting earlier ones), e.g. occurrence 2 = "the loop has re-parked on a fresh timer after the first fired".
    /// </summary>
    public Task TimerCreatedAsync(TimeSpan dueTime, int occurrence = 1)
    {
        lock (_sync)
        {
            if (CountLocked(dueTime) >= occurrence)
            {
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((dueTime, occurrence, signal));
            return signal.Task;
        }
    }

    private int CountLocked(TimeSpan dueTime) => _created.Count(created => created == dueTime);
}
