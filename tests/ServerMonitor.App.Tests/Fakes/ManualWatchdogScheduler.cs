using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// Deterministic time for the REAL <see cref="TerminationWatchdog"/>.
/// <para>
/// This is the only thing the watchdog tests are allowed to replace. Fake TIME is acceptable; a fake
/// WATCHDOG is not — an earlier round exercised a <c>FakeTerminationWatchdog</c>, and a mutant that made
/// the production watchdog expire immediately still passed 542/542. The state machine under test is
/// therefore always the production class, and only its waiting is made controllable here (BOSS.md §10).
/// </para>
/// <para>
/// Not thread-safe: <see cref="Attempts"/> and the schedule list are only valid while callers are
/// serialized, which production does under <c>TerminationWatchdog._armGate</c>.
/// </para>
/// </summary>
internal sealed class ManualWatchdogScheduler : IWatchdogScheduler
{
    private readonly List<(TimeSpan Delay, Action Callback)> _scheduled = new();

    /// <summary>Every deadline the watchdog asked for, in order. A second entry means it was restarted.</summary>
    public IReadOnlyList<TimeSpan> ScheduledDelays => _scheduled.Select(entry => entry.Delay).ToArray();

    /// <summary>How many deadlines were actually ESTABLISHED. A failed attempt establishes nothing.</summary>
    public int ScheduleCount => _scheduled.Count;

    /// <summary>Every attempt, successful or not. The gap between the two is the CV-21 A case.</summary>
    public int Attempts { get; private set; }

    /// <summary>
    /// When above zero, that many further attempts throw instead of scheduling - a watchdog thread that
    /// cannot be started, which is what made the old code claim to be armed with nothing behind it.
    /// </summary>
    public int FailNextAttempts { get; set; }

    /// <summary>
    /// Runs INSIDE the attempt, before it succeeds or fails, so a test can interleave a second caller at
    /// the exact moment the first is mid-arming.
    /// </summary>
    public Action? OnScheduleAttempt { get; set; }

    public void ScheduleOnce(TimeSpan delay, Action callback)
    {
        Attempts++;
        OnScheduleAttempt?.Invoke();

        if (FailNextAttempts > 0)
        {
            FailNextAttempts--;
            throw new InvalidOperationException("the watchdog thread could not be started");
        }

        _scheduled.Add((delay, callback));
    }

    /// <summary>
    /// Fires everything scheduled, exactly as elapsed time would. Callbacks are taken by copy so a
    /// callback that schedules more work cannot mutate the list mid-iteration.
    /// </summary>
    public void ElapseAll()
    {
        foreach (var (_, callback) in _scheduled.ToArray())
        {
            callback();
        }
    }
}
