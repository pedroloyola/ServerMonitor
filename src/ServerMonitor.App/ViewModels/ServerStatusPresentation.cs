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
    /// The rows show "—" instead of a retained snapshot that is not a current reading, so an old value never reads as
    /// current: an Offline server, and (UI.12 STATE-03) a "Sem dados" (Unknown) server whose last cycle FAILED - the engine
    /// keeps the previous snapshot then (NonRetryable / no data), and it used to show with severity colour and no cue. The
    /// Detail page shows that reading instead, marked stale (H-UI5-2) — see <see cref="ShowsRetainedReadingAsStale"/>.
    /// </summary>
    public static bool RowHidesRetainedMetrics(ServerHealth health, bool hasLastError) =>
        health == ServerHealth.Offline || (health == ServerHealth.Unknown && hasLastError);

    /// <summary>
    /// H-UI5-2: the Detail page keeps a retained reading visible but marks it stale whenever the engine says it is stale,
    /// or the server is without connection (Offline), or (UI.12 STATE-03) "Sem dados" after a failed cycle, while a
    /// snapshot is still held. Never a new health state.
    /// </summary>
    public static bool ShowsRetainedReadingAsStale(ServerHealth health, bool isStale, bool hasMetrics, bool hasLastError) =>
        hasMetrics && (isStale || RowHidesRetainedMetrics(health, hasLastError));

    /// <summary>
    /// UI.12 B12-06 (Prism decision §3): a row that still SHOWS retained values (not hidden by
    /// <see cref="RowHidesRetainedMetrics"/>) and is stale says so - the Compact R-7 cue "Última atualização há N min"
    /// (<see cref="ServerCardViewModel.StaleAgeDisplay"/>). Null when there is nothing to say.
    /// </summary>
    public static string? RowStaleAge(ServerCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return card.HasStaleIndicator && !RowHidesRetainedMetrics(card.Health, card.LastError is not null)
            ? card.StaleAgeDisplay
            : null;
    }

    /// <summary>
    /// Cortex B1 N-5: the accessible value of one metric, shared by the rows and the Detail ("22%", or the "sem dados"
    /// text when the percentage is unknown - never 0).
    /// </summary>
    public static string AccessiblePercent(bool known, double value, ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        return known
            ? string.Format(System.Globalization.CultureInfo.CurrentUICulture, "{0:0}%", value)
            : localization.GetString("ServerMetricUnavailableAccessible");
    }
}
