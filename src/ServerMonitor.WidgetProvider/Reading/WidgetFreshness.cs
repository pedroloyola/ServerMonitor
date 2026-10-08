using ServerMonitor.WidgetContract;

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
/// Derives fresh/stale/unavailable at runtime from the snapshot's timestamps and the PROVIDER's own clock
/// (§21) — never persisted, and never the app's frozen <c>IsStale</c>, which stops being true the moment
/// the app stops writing. Freshness is deliberately separate from health: a Healthy server with a stale
/// snapshot stays Healthy, is never escalated to Warning/Critical (§22), and is never presented as
/// currently healthy either.
/// <para>
/// UI.9 D-UI9-4: the snapshot threshold is DERIVED, not fixed — <c>max(90 s, max_i staleAfterSeconds_i)</c>
/// — so a fleet monitored every 60/300 s is not falsely "out of date" between its own cycles. An old file
/// (no per-server value) or an empty fleet keeps 90 s. The cost is accepted and documented: with a fleet at
/// 300 s, detecting a stopped app takes up to 600 s. The policy itself lives in the app (StalePolicy); the
/// provider only compares ages with the numbers the snapshot carries.
/// </para>
/// </summary>
public static class WidgetFreshness
{
    /// <summary>Floor of the derived snapshot threshold, and the whole threshold for old files.</summary>
    public static readonly TimeSpan DefaultStaleThreshold = TimeSpan.FromSeconds(90);

    /// <summary>D-UI9-4: <c>max(90 s, max_i staleAfterSeconds_i)</c> over the validated snapshot.</summary>
    public static TimeSpan DeriveSnapshotThreshold(WidgetStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var threshold = DefaultStaleThreshold;
        foreach (var server in snapshot.Servers)
        {
            if (server.StaleAfterSeconds is { } seconds && TimeSpan.FromSeconds(seconds) > threshold)
            {
                threshold = TimeSpan.FromSeconds(seconds);
            }
        }

        return threshold;
    }

    /// <summary>
    /// Snapshot freshness. <paramref name="staleThreshold"/> overrides the derived threshold (tests and
    /// explicit composition only); <c>null</c> derives it from the snapshot (D-UI9-4).
    /// </summary>
    public static WidgetFreshnessState Evaluate(
        WidgetReadResult read,
        DateTimeOffset nowUtc,
        TimeSpan? staleThreshold = null)
    {
        if (!read.IsAvailable || read.Snapshot is null)
        {
            return WidgetFreshnessState.Unavailable;
        }

        var threshold = staleThreshold ?? DeriveSnapshotThreshold(read.Snapshot);
        var age = nowUtc - read.Snapshot.GeneratedAtUtc;

        // A snapshot slightly in the future (clock skew) is treated as fresh; the validator already
        // rejected implausible timestamps, so a negative age here is small.
        return age <= threshold ? WidgetFreshnessState.Fresh : WidgetFreshnessState.Stale;
    }

    /// <summary>
    /// Per-server freshness (D-UI9-5, V-RC-5): a server is fresh only if the snapshot is fresh (a server is
    /// never fresher than its snapshot), it HAS a reading (<c>null</c> is never fresh), and that reading is
    /// no older than its own <c>staleAfterSeconds</c> — or, for an old file without the field, the snapshot
    /// threshold.
    /// </summary>
    public static bool IsServerFresh(
        WidgetServerState server,
        WidgetFreshnessState snapshotFreshness,
        TimeSpan snapshotThreshold,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (snapshotFreshness != WidgetFreshnessState.Fresh || server.LastUpdatedUtc is not { } lastUpdated)
        {
            return false;
        }

        var threshold = server.StaleAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : snapshotThreshold;
        return nowUtc - lastUpdated <= threshold;
    }
}
