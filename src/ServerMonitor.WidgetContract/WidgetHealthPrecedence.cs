namespace ServerMonitor.WidgetContract;

/// <summary>
/// Deterministic fleet-overall health (§21). Precedence, most-to-least alarming at a fleet glance:
/// <c>Offline &gt; Critical &gt; Warning &gt; Unknown &gt; Healthy</c>.
/// <para>
/// Rationale (documented so the choice is auditable): a server the operator cannot see at all
/// (<see cref="WidgetHealth.Offline"/>) is the most attention-worthy, ahead of a critical-threshold
/// breach, then a warning. Crucially <see cref="WidgetHealth.Unknown"/> outranks
/// <see cref="WidgetHealth.Healthy"/>: Unknown means "insufficient information", so the fleet may only be
/// reported <see cref="WidgetHealth.Healthy"/> when NOTHING in it is Offline/Critical/Warning/Unknown.
/// An empty fleet is <see cref="WidgetHealth.Unknown"/>.
/// </para>
/// Worked examples: Healthy+Healthy→Healthy; Healthy+Unknown→Unknown; Healthy+Warning→Warning;
/// Unknown+Warning→Warning; Critical+Unknown→Critical; Offline+Critical→Offline; Offline+Unknown→Offline.
/// <para>
/// This is a pure function reused by the writer (to precompute <see cref="WidgetStateSnapshot.OverallHealth"/>)
/// and by the provider, so both always agree. It aggregates only the widget OverallHealth; per-server
/// health semantics are unchanged.
/// </para>
/// </summary>
public static class WidgetHealthPrecedence
{
    /// <summary>Returns the worst health by fleet precedence, or <see cref="WidgetHealth.Unknown"/> if empty.</summary>
    public static WidgetHealth Worst(IEnumerable<WidgetHealth> healths)
    {
        ArgumentNullException.ThrowIfNull(healths);

        // Compare by rank and return the CANONICAL enum for the winning rank — never the original input
        // value. This keeps the result order-independent and always a defined enum even if an undefined
        // value slips in (it ranks as Unknown), rather than echoing an invalid value back out.
        int? worstRank = null;
        foreach (var health in healths)
        {
            var rank = Rank(health);
            if (worstRank is null || rank > worstRank.Value)
            {
                worstRank = rank;
            }
        }

        return worstRank is { } winner ? FromRank(winner) : WidgetHealth.Unknown;
    }

    /// <summary>
    /// Explicit severity rank, independent of the enum's numeric values, so the precedence is readable
    /// and cannot silently change if the enum is reordered. Higher rank wins. Any undefined value ranks
    /// as <see cref="WidgetHealth.Unknown"/> (insufficient information).
    /// </summary>
    public static int Rank(WidgetHealth health) => health switch
    {
        WidgetHealth.Healthy => 0,
        WidgetHealth.Unknown => 1,
        WidgetHealth.Warning => 2,
        WidgetHealth.Critical => 3,
        WidgetHealth.Offline => 4,
        _ => 1
    };

    private static WidgetHealth FromRank(int rank) => rank switch
    {
        0 => WidgetHealth.Healthy,
        2 => WidgetHealth.Warning,
        3 => WidgetHealth.Critical,
        4 => WidgetHealth.Offline,
        _ => WidgetHealth.Unknown
    };
}
