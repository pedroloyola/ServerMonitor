using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Services;

/// <summary>
/// Tuning for the <see cref="MonitoringEngine"/>. Defaults are deliberately modest so idle
/// CPU stays near zero and no server is hammered. All durations are honoured through the
/// injected <see cref="TimeProvider"/>, so tests can advance them deterministically.
/// </summary>
public sealed record MonitoringOptions
{
    /// <summary>Maximum concurrent collections across all servers.</summary>
    public int MaxConcurrentCollections { get; init; } = 4;

    /// <summary>Delay before a server's first collection after monitoring starts.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Extra delay added per server so many servers do not all fire at once.</summary>
    public TimeSpan StartupStagger { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Delays before retry attempts 2, 3, … within one cycle. Only transient failures retry.
    /// [1s, 3s] means: attempt, +1s, +3s, then give up (Offline).
    /// </summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    /// <summary>
    /// Interval used for the next attempt after a non-transient failure (auth, host-key,
    /// bad config, unsupported OS). Longer than the normal interval so a broken server is
    /// not re-attempted every cycle, while still allowing eventual recovery once fixed.
    /// </summary>
    public TimeSpan AttentionInterval { get; init; } = TimeSpan.FromMinutes(5);

    public MonitoringThresholds Thresholds { get; init; } = MonitoringThresholds.Default;

    public int MaxAttemptsPerCycle => RetryDelays.Count + 1;

    public static MonitoringOptions Default { get; } = new();
}
