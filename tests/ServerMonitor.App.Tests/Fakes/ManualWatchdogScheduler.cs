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
/// </summary>
internal sealed class ManualWatchdogScheduler : IWatchdogScheduler
{
    private readonly List<(TimeSpan Delay, Action Callback)> _scheduled = new();

    /// <summary>Every deadline the watchdog asked for, in order. A second entry means it was restarted.</summary>
    public IReadOnlyList<TimeSpan> ScheduledDelays => _scheduled.Select(entry => entry.Delay).ToArray();

    public int ScheduleCount => _scheduled.Count;

    public void ScheduleOnce(TimeSpan delay, Action callback) => _scheduled.Add((delay, callback));

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
