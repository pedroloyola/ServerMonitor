using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY wiring for the M6 visual health harness. Activated only when the app is launched with
/// <see cref="LaunchFlag"/>. It replaces the four data-plane services with inert in-memory doubles
/// and pre-seeds the monitoring-state store from the scenario catalog. Because the real
/// <c>MonitoringEngine</c> and its hosted service are not registered in QA mode (the caller guards
/// them), no SSH, scheduling, persistence or credential access ever runs. Excluded from Release.
/// </summary>
internal static class QaHealthComposition
{
    public const string LaunchFlag = "--qa-health";

    public static bool IsRequested() =>
        Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, LaunchFlag, StringComparison.OrdinalIgnoreCase));

    public static void Apply(IServiceCollection services)
    {
        // Registered last so they win over the real registrations for every resolve.
        services.AddSingleton<IServerService, QaServerService>();
        services.AddSingleton<IServerMetricsStore, QaMetricsStore>();
        services.AddSingleton<IMonitoringEngine, QaMonitoringEngine>();

        // The dashboard depends on discovery too; register an inert, empty one so the health
        // harness resolves without any real mDNS/SSH running and shows no suggestions.
        services.AddSingleton<IServerDiscoveryService>(new QaDiscoveryService([]));

        var stateStore = new ServerMonitoringStateStore();
        foreach (var scenario in QaHealthCatalog.Scenarios)
        {
            stateStore.Set(scenario.State);
        }

        services.AddSingleton<IServerMonitoringStateStore>(stateStore);
    }
}
