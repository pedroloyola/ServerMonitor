using Microsoft.Extensions.Time.Testing;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that reports when a timer with a given due time has been registered, so a test
/// can advance the clock exactly once AFTER the code under test is parked on that timer (instead of advancing
/// repeatedly until something happens, or advancing inside the window between reading the clock and arming the timer,
/// which would arm the timer relative to the ALREADY-advanced clock).
/// </summary>
internal sealed class TimerRecordingTimeProvider : FakeTimeProvider
{
    private readonly object _sync = new();
    private readonly List<TimeSpan> _created = [];
    private readonly List<(TimeSpan DueTime, TaskCompletionSource Signal)> _waiters = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period); // registered before anyone is told
        lock (_sync)
        {
            _created.Add(dueTime);
            foreach (var waiter in _waiters.Where(waiter => waiter.DueTime == dueTime).ToArray())
            {
                waiter.Signal.TrySetResult();
                _waiters.Remove(waiter);
            }
        }

        return timer;
    }

    /// <summary>Completes once a timer with <paramref name="dueTime"/> has been created (including earlier ones).</summary>
    public Task TimerCreatedAsync(TimeSpan dueTime)
    {
        lock (_sync)
        {
            if (_created.Contains(dueTime))
            {
                return Task.CompletedTask;
            }

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((dueTime, signal));
            return signal.Task;
        }
    }
}
