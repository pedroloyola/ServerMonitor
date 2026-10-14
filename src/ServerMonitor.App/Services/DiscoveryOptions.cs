namespace ServerMonitor.App.Services;

/// <summary>
/// Tuning for <see cref="ServerDiscoveryService"/>. Tmds.MDns does not honour DNS TTLs and does
/// not surface a query cadence, so expiry and removal grace are enforced here, on top of the
/// injected <see cref="TimeProvider"/> so tests can advance them deterministically. Defaults are
/// deliberately quiet: passive-first, no aggressive traffic.
/// </summary>
public sealed record DiscoveryOptions
{
    /// <summary>
    /// How long a per-interface observation survives without being seen again before it is
    /// expired. Sized around three missed ~30 s announcement cycles, plus slack.
    /// </summary>
    public TimeSpan ExpiryWindow { get; init; } = TimeSpan.FromSeconds(95);

    /// <summary>
    /// Grace period after a goodbye/removal before the observation is actually dropped, so a
    /// brief flap or an immediate re-announcement does not make a suggestion blink.
    /// </summary>
    public TimeSpan RemovalGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the store sweeps for expired/grace-elapsed observations.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Short coalescing window for material browser/store changes. The service clamps this to
    /// the range 1 ms–1 s so a hostile announcement burst cannot create an unbounded UI event
    /// stream or defer an update indefinitely.
    /// </summary>
    public TimeSpan ChangeNotificationDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// REL.0B: a user-started search that shows nothing for this long ends by itself as
    /// <see cref="DiscoverySearchEnd.NothingFound"/>. Sized to cover the browse's first query and one
    /// re-query at the 30 s cadence (<c>MdnsServiceBrowserOptions.QueryInterval</c>), plus margin.
    /// </summary>
    public TimeSpan NothingFoundTimeout { get; init; } = TimeSpan.FromSeconds(40);

    /// <summary>
    /// REL.0B: hard upper bound on one user-started search, results or not. The UDP 5353 listener
    /// processes untrusted LAN multicast, so it never stays open for a whole foreground session;
    /// the user can search again.
    /// </summary>
    public TimeSpan MaxSearchDuration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Bounded drain timeout for the sweep loop during shutdown.</summary>
    public TimeSpan StopDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public static DiscoveryOptions Default { get; } = new();
}
