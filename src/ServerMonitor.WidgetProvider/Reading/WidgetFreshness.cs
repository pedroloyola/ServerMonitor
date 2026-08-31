namespace ServerMonitor.WidgetProvider.Reading;

/// <summary>How current the snapshot is, independent of health (§22 — stale is freshness, not health).</summary>
public enum WidgetFreshnessState
{
    /// <summary>A valid snapshot generated within the stale threshold.</summary>
    Fresh,

    /// <summary>A valid snapshot, but older than the threshold (app may be closed or paused).</summary>
    Stale,

    /// <summary>No usable snapshot at all.</summary>
    Unavailable
}

/// <summary>
/// Derives fresh/stale/unavailable at runtime from <c>GeneratedAtUtc</c> and the current clock (§21) —
/// never persisted (§21). The default threshold is three ~30 s monitoring cycles. Freshness is
/// deliberately separate from health: a Healthy server with a stale snapshot stays Healthy and is shown
/// as "updated N ago", never escalated to Warning/Critical (§22).
/// </summary>
public static class WidgetFreshness
{
    public static readonly TimeSpan DefaultStaleThreshold = TimeSpan.FromSeconds(90);

    public static WidgetFreshnessState Evaluate(
        WidgetReadResult read,
        DateTimeOffset nowUtc,
        TimeSpan? staleThreshold = null)
    {
        if (!read.IsAvailable || read.Snapshot is null)
        {
            return WidgetFreshnessState.Unavailable;
        }

        var threshold = staleThreshold ?? DefaultStaleThreshold;
        var age = nowUtc - read.Snapshot.GeneratedAtUtc;

        // A snapshot slightly in the future (clock skew) is treated as fresh; the validator already
        // rejected implausible timestamps, so a negative age here is small.
        return age <= threshold ? WidgetFreshnessState.Fresh : WidgetFreshnessState.Stale;
    }
}
