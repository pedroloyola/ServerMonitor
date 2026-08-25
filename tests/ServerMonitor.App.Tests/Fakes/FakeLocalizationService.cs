using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// Deterministic stand-in for <see cref="ILocalizationService"/>. It does not
/// couple tests to the real .resw copy: label keys resolve to the key itself,
/// while the handful of format keys the metrics UI relies on resolve to a
/// canonical composite-format string. This keeps assertions about the
/// ViewModel's own logic (which bucket, which arguments) stable even if the
/// visible wording changes, while still exercising the localization path.
/// </summary>
internal sealed class FakeLocalizationService : ILocalizationService
{
    private static readonly IReadOnlyDictionary<string, string> Formats =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ServerCardMoreOptionsFor"] = "More options for {0}",
            ["ServerMetricsRefreshFor"] = "Refresh metrics for {0}",
            ["ServerCardAutomationSummary"] = "{0} · {1} · {2} · {3}",
            ["ServerMetricsUptimeDaysHoursFormat"] = "{0}d {1}h",
            ["ServerMetricsUptimeHoursMinutesFormat"] = "{0}h {1}m",
            ["ServerMetricsUptimeMinutesFormat"] = "{0}m",
            ["ServerMetricsDetectedOperatingSystemFormat"] = "OS: {0}",
            ["ServerMetricsUpdatedAtFormat"] = "Updated {0}",
            ["ServerMetricsUpdateFailed"] = "Metrics could not be refreshed.",
            ["ServerMetricsStaleMinutesFormat"] = "Last updated {0} min ago",
            ["ServerMetricsStaleHoursFormat"] = "Last updated {0} h ago",
            ["ServerMetricsStaleDaysFormat"] = "Last updated {0} d ago",
        };

    public string? CurrentLanguageOverride => null;

    public string GetString(string resourceKey) =>
        Formats.TryGetValue(resourceKey, out var value) ? value : resourceKey;

    public void InitializeFromSystem()
    {
    }

    public void SetLanguage(string? languageTag)
    {
    }
}
