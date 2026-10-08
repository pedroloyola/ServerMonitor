namespace ServerMonitor.WidgetContract;

/// <summary>
/// Constants for the widget state snapshot wire contract (ADR-018). This is a <b>persisted wire
/// contract</b> written by the running app and read by the out-of-process widget provider: it must be
/// small, explicitly versioned (§11), and bounded (§18). A reader treats the file as untrusted (§17)
/// and validates every value against these limits before use.
/// </summary>
public static class WidgetSchema
{
    /// <summary>
    /// Current schema version. A reader MUST accept only this value and fail neutral for anything else,
    /// rather than guess how to interpret an unknown schema (§11).
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Upper bound on servers carried in one snapshot. Defense-in-depth on read (§18): a file claiming
    /// more than this is treated as malformed. Comfortably above a realistic fleet.
    /// </summary>
    public const int MaxServers = 100;

    /// <summary>Maximum length of a sanitized display name, in UTF-16 code units (§10/§18).</summary>
    public const int MaxDisplayNameLength = 60;

    /// <summary>
    /// Lowest plausible generation/update timestamp. Anything at or before this is treated as invalid
    /// (§18/§22) — it predates the product and indicates an old, altered, or corrupt file.
    /// </summary>
    public static readonly DateTimeOffset MinTimestampUtc = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Allowed clock skew when validating that a timestamp is not implausibly in the future (§18/§22).
    /// The producer and reader may be different processes reading slightly different clocks.
    /// </summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Allowed lead of a server's <c>lastUpdatedUtc</c> over the snapshot's <c>generatedAtUtc</c> (UI.9
    /// V-RC-5). The producer reads both from one clock with the reading strictly before the write, so a
    /// server can never be fresher than the snapshot that carries it; the second only absorbs rounding.
    /// </summary>
    public static readonly TimeSpan MaxLastUpdatedLead = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Bounds of the optional per-server <c>staleAfterSeconds</c> (UI.9 D-UI9-3, V-RC-4) — exactly the
    /// domain of the app's stale policy over its supported refresh intervals (20 s floor .. 2 × 300 s).
    /// Anything outside is rejected as out of range, so one bad value cannot stretch the derived snapshot
    /// threshold (D-UI9-4). The app proves its policy stays inside these bounds by test.
    /// </summary>
    public const int MinStaleAfterSeconds = 20;

    /// <inheritdoc cref="MinStaleAfterSeconds"/>
    public const int MaxStaleAfterSeconds = 600;
}
