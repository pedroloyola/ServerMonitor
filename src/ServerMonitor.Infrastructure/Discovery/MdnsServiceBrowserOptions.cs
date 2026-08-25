namespace ServerMonitor.Infrastructure.Discovery;

/// <summary>
/// Configuration for <see cref="TmdsMdnsServiceBrowser"/>. Only the SSH service type is browsed;
/// there is no port scan and no arbitrary service-type enumeration. The library appends the
/// local (.local.) domain itself.
/// </summary>
public sealed record MdnsServiceBrowserOptions
{
    /// <summary>Lower bound for the passive re-query interval.</summary>
    public static readonly TimeSpan MinQueryInterval = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound for the passive re-query interval.</summary>
    public static readonly TimeSpan MaxQueryInterval = TimeSpan.FromMinutes(5);

    /// <summary>The DNS-SD service type to browse. SSH only, by design.</summary>
    public string ServiceType { get; init; } = "_ssh._tcp";

    /// <summary>
    /// Passive re-query cadence for the browser. Tmds.MDns defaults this to 10 s; we raise it to a
    /// calmer 30 s so discovery stays non-aggressive while still refreshing steadily. The runtime
    /// store's expiry window is sized around three of these intervals. Always applied through
    /// <see cref="ResolveQueryIntervalMilliseconds"/>, which keeps it positive and bounded.
    /// </summary>
    public TimeSpan QueryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Returns the query interval in whole milliseconds, clamped to
    /// [<see cref="MinQueryInterval"/>, <see cref="MaxQueryInterval"/>] so a misconfigured or
    /// non-positive value can never disable re-querying or flood the link.
    /// </summary>
    public int ResolveQueryIntervalMilliseconds()
    {
        var interval = QueryInterval;
        if (interval < MinQueryInterval)
        {
            interval = MinQueryInterval;
        }
        else if (interval > MaxQueryInterval)
        {
            interval = MaxQueryInterval;
        }

        return (int)interval.TotalMilliseconds;
    }

    public static MdnsServiceBrowserOptions Default { get; } = new();
}
