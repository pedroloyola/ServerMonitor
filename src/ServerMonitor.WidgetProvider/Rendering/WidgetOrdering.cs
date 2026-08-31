using ServerMonitor.WidgetContract;

namespace ServerMonitor.WidgetProvider.Rendering;

/// <summary>
/// Deterministic display order for the Medium/Large widgets (§10): most-attention-worthy first —
/// <c>Offline &gt; Critical &gt; Warning &gt; Unknown &gt; Healthy</c> — then by sanitized display name
/// (ordinal, culture-invariant), then by opaque id as a final stable tiebreak. So problems always appear
/// first and the order never shuffles between updates for the same fleet.
/// </summary>
public static class WidgetOrdering
{
    public static IReadOnlyList<WidgetServerState> ForDisplay(IReadOnlyList<WidgetServerState> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);

        return servers
            .OrderBy(DisplayRank)
            .ThenBy(s => s.DisplayName, StringComparer.Ordinal)
            .ThenBy(s => s.Id)
            .ToArray();
    }

    /// <summary>Lower rank is shown first: problems before healthy servers.</summary>
    public static int DisplayRank(WidgetServerState server) => server.Health switch
    {
        WidgetHealth.Offline => 0,
        WidgetHealth.Critical => 1,
        WidgetHealth.Warning => 2,
        WidgetHealth.Unknown => 3,
        WidgetHealth.Healthy => 4,
        _ => 3
    };
}
