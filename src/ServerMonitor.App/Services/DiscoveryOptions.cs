namespace ServerMonitor.App.Services;

/// <summary>
/// Tuning for <see cref="ServerDiscoveryService"/>. Tmds.MDns does not honour DNS TTLs and does
/// not surface a query cadence, so the search bounds, the expiry backstop and removal grace are
/// enforced here, on top of the injected <see cref="TimeProvider"/> so tests can advance them
/// deterministically. Defaults are deliberately quiet: passive-first, no aggressive traffic.
/// </summary>
public sealed record DiscoveryOptions
{
    /// <summary>
    /// How long a per-interface observation survives without a new Found/Updated event before it is
    /// expired. REL.0D O-8: longer than <see cref="MaxSearchDuration"/> (plus a 1 min timing margin), so
    /// it never fires inside a search. Tmds.MDns 0.9.1 raises no event when a re-query answer repeats
    /// unchanged records, so "not seen again" is not observable within a search: a shorter window
    /// dropped every live suggestion ~95 s after it appeared. Liveness comes from the library instead
    /// (goodbye/TTL 0 and its unanswered-query robustness removal raise Removed, then
    /// <see cref="RemovalGrace"/>), and the end of the search clears everything. A device that vanishes
    /// silently may stay listed until the search ends; adding it still goes through the normal
    /// connection test and host-key trust.
    /// </summary>
    public TimeSpan ExpiryWindow { get; init; } = TimeSpan.FromMinutes(11);

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
