using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY wiring for the M10 history/charts harness. Activated only with <see cref="LaunchFlag"/>.
/// It replaces the data plane and the history query service with deterministic in-memory doubles, so
/// the real HistoryPage and charts can be inspected across every data shape (spike, offline gap, null
/// RAM, empty, unavailable, 1h/7d/30d) without SSH, scheduling, persistence or waiting days. The real
/// MonitoringEngine, history recorder/writer and SQLite store are not registered in QA mode (the
/// caller guards them). Excluded from Release.
/// </summary>
internal static class QaHistoryComposition
{
    public const string LaunchFlag = "--qa-history";

    public static bool IsRequested() =>
        Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, LaunchFlag, StringComparison.OrdinalIgnoreCase));

    public static void Apply(IServiceCollection services)
    {
        // Registered last so they win over the real registrations for every resolve.
        services.AddSingleton<IServerService, QaHistoryServerService>();
        services.AddSingleton<IServerMetricsStore, QaHistoryMetricsStore>();
        services.AddSingleton<IMonitoringEngine, QaMonitoringEngine>();
        services.AddSingleton<IServerDiscoveryService>(new QaDiscoveryService([]));
        services.AddSingleton<IServerHistoryQueryService, QaServerHistoryQueryService>();

        var stateStore = new ServerMonitoringStateStore();
        foreach (var scenario in QaHistoryCatalog.Scenarios)
        {
            stateStore.Set(scenario.State);
        }

        services.AddSingleton<IServerMonitoringStateStore>(stateStore);
    }
}
