using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY wiring for the network-discovery harness, activated only under <see cref="LaunchFlag"/>.
/// It registers a no-servers data plane plus inert monitoring, so no SSH, scheduling, persistence
/// or credential access ever runs, and a seeded in-memory <see cref="IServerDiscoveryService"/> so
/// the real dashboard discovery section and card can be driven with two deterministic suggestions.
/// Excluded from Release.
/// </summary>
internal static class QaDiscoveryComposition
{
    public const string LaunchFlag = "--qa-discovery";

    public static bool IsRequested() =>
        Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, LaunchFlag, StringComparison.OrdinalIgnoreCase));

    public static void Apply(IServiceCollection services)
    {
        // Registered last so they win over the real registrations for every resolve.
        services.AddSingleton<IServerService, QaNoServersService>();
        services.AddSingleton<IServerMetricsStore, QaMetricsStore>();
        services.AddSingleton<IServerMonitoringStateStore, ServerMonitoringStateStore>();
        services.AddSingleton<IMonitoringEngine, QaMonitoringEngine>();

        services.AddSingleton<IServerDiscoveryService>(new QaDiscoveryService(QaDiscoveryCatalog.Seed()));
    }
}
