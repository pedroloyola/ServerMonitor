using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// UI.5 §3: the ONE place the engine-owned <see cref="ServerHealth"/> becomes the visible status copy ("Saudável",
/// "Atenção", "Crítico", "Sem ligação", "Sem dados"). The Visão geral / Servidores rows and the Server Detail page both
/// read it here, so they can never diverge; "Offline" (<c>ServerHealth*</c>) is no longer shown by either. This is the
/// same table the rows already used — no new health classification.
/// </summary>
public static class ServerStatusPresentation
{
    /// <summary>The resource key of a health's status copy. An undefined value reads as "Sem dados", never as healthy.</summary>
    public static string StatusKey(ServerHealth health) =>
        "ServerStatus" + (Enum.IsDefined(health) ? health : ServerHealth.Unknown);

    public static string StatusText(ServerHealth health, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        return localization.GetString(StatusKey(health));
    }

    /// <summary>
    /// The rows (no staleness cue) show "—" instead of an Offline server's retained snapshot, so an old value never reads
    /// as current. The Detail page shows that reading instead, marked stale (H-UI5-2) — see
    /// <see cref="ShowsRetainedReadingAsStale"/>.
    /// </summary>
    public static bool RowHidesRetainedMetrics(ServerHealth health) => health == ServerHealth.Offline;

    /// <summary>
    /// H-UI5-2: the Detail page keeps a retained reading visible but marks it stale whenever the engine says it is stale,
    /// or the server is without connection (Offline) while a snapshot is still held. Never a new health state.
    /// </summary>
    public static bool ShowsRetainedReadingAsStale(ServerHealth health, bool isStale, bool hasMetrics) =>
        hasMetrics && (isStale || health == ServerHealth.Offline);
}
