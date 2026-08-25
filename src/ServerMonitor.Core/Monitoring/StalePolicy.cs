namespace ServerMonitor.Core.Monitoring;

/// <summary>
/// Decides when the last successful metrics reading is too old to be trusted as current.
/// A reading is stale once its age exceeds roughly twice the server's refresh interval,
/// with a small floor so very short intervals do not flap on a single jittery cycle.
/// A server that never succeeded is not "stale" — it is pending/unknown, handled elsewhere.
/// </summary>
public static class StalePolicy
{
    private static readonly TimeSpan Floor = TimeSpan.FromSeconds(20);

    public static TimeSpan StaleAfter(TimeSpan interval)
    {
        var twice = interval + interval;
        return twice > Floor ? twice : Floor;
    }

    public static bool IsStale(DateTimeOffset? lastSuccessAt, DateTimeOffset now, TimeSpan interval)
    {
        if (lastSuccessAt is not { } lastSuccess)
        {
            return false;
        }

        return now - lastSuccess > StaleAfter(interval);
    }
}
