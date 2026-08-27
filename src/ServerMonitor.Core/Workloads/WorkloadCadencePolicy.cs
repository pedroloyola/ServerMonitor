namespace ServerMonitor.Core.Workloads;

/// <summary>
/// Decides how often workload collection is <i>due</i> for a server, independent of the (possibly
/// faster) host polling interval. Pure and deterministic, mirroring the M10 <c>HistorySamplingPolicy</c>:
/// at most one scheduled workload collection per <see cref="MinInterval"/> per server. Riding the M6
/// cycle signal with this throttle means workloads never collect faster than the host, never faster than
/// <see cref="MinInterval"/>, and add no timer of their own (§34/§35). Manual refresh bypasses this
/// policy entirely.
/// </summary>
public sealed class WorkloadCadencePolicy
{
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(60);

    public WorkloadCadencePolicy(TimeSpan? minInterval = null)
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
    /// True when a scheduled collection at <paramref name="candidateUtc"/> is due, given the last time
    /// one was enqueued for that server (<paramref name="lastEnqueuedUtc"/>, or <c>null</c> if never). A
    /// candidate at or before the last stamp is never due (guards duplicate/replayed cycles and
    /// non-monotonic clocks).
    /// </summary>
    public bool IsDue(DateTimeOffset? lastEnqueuedUtc, DateTimeOffset candidateUtc)
    {
        if (lastEnqueuedUtc is null)
        {
            return true;
        }

        return candidateUtc - lastEnqueuedUtc.Value >= MinInterval;
    }
}
