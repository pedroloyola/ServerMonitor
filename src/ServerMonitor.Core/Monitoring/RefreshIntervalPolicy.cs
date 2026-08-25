namespace ServerMonitor.Core.Monitoring;

/// <summary>
/// The refresh intervals the app supports for automatic monitoring. Persisted per
/// server as a plain number of seconds. <see cref="Normalize"/> guards against values
/// from hand-edited or older configuration: nothing faster than 10s is ever used, and
/// out-of-catalog values snap to the nearest supported one. Absent/zero means "use the
/// default", which is how existing <c>servers.json</c> entries migrate to 30s.
/// </summary>
public static class RefreshIntervalPolicy
{
    public const int MinimumSeconds = 10;

    public const int DefaultSeconds = 30;

    public static IReadOnlyList<int> SupportedSeconds { get; } = [10, 30, 60, 300];

    public static int Normalize(int seconds)
    {
        if (seconds <= 0)
        {
            return DefaultSeconds;
        }

        if (SupportedSeconds.Contains(seconds))
        {
            return seconds;
        }

        if (seconds < MinimumSeconds)
        {
            return MinimumSeconds;
        }

        // Snap to the nearest supported value; on a tie prefer the slower (safer) one.
        var best = SupportedSeconds[0];
        var bestDistance = int.MaxValue;
        foreach (var candidate in SupportedSeconds)
        {
            var distance = Math.Abs(candidate - seconds);
            if (distance < bestDistance || (distance == bestDistance && candidate > best))
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    public static TimeSpan ToInterval(int seconds) => TimeSpan.FromSeconds(Normalize(seconds));
}
