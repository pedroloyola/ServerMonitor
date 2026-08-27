namespace ServerMonitor.Core.History;

/// <summary>
/// Decides how often a completed cycle is persisted to history, independent of the (faster) polling
/// interval. Pure and deterministic (ADR-015 §6; spec §16): at most one persisted sample per
/// <see cref="MinInterval"/> per server. Poll at 10s ⇒ ~1 in 3 cycles persists; poll ≥ 30s ⇒ every
/// useful cycle persists. Keeps the DB bounded with good 1h/6h resolution and low write
/// amplification.
/// </summary>
public sealed class HistorySamplingPolicy
{
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(30);

    public HistorySamplingPolicy(TimeSpan? minInterval = null)
    {
        var interval = minInterval ?? DefaultMinInterval;
        if (interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minInterval));
        }

        MinInterval = interval;
    }

    public TimeSpan MinInterval { get; }

    /// <summary>
    /// True when a candidate cycle at <paramref name="candidateUtc"/> should be persisted given the
    /// timestamp of the last persisted sample for that server (<paramref name="lastPersistedUtc"/>,
    /// or <c>null</c> if none yet). A candidate at or before the last persisted timestamp is never
    /// re-persisted (guards duplicate/replayed events and non-monotonic clocks).
    /// </summary>
    public bool ShouldPersist(DateTimeOffset? lastPersistedUtc, DateTimeOffset candidateUtc)
    {
        if (lastPersistedUtc is null)
        {
            return true;
        }

        return candidateUtc - lastPersistedUtc.Value >= MinInterval;
    }
}
