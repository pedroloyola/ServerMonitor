using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Qa;

/// <summary>
/// Debug-only deterministic notification harness. It replaces every real data-plane and
/// persistence service with in-memory doubles; the real M8 policy and Windows notification
/// adapter still run so the desktop presentation can be verified safely.
/// </summary>
internal static class QaNotificationComposition
{
    public const string LaunchFlag = "--qa-notifications";

    public static bool IsRequested() =>
        Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, LaunchFlag, StringComparison.OrdinalIgnoreCase));

    public static void Apply(IServiceCollection services)
    {
        services.AddSingleton<IServerService, QaNotificationServerService>();
        services.AddSingleton<IServerMetricsStore, QaMetricsStore>();
        services.AddSingleton<IMonitoringEngine, QaMonitoringEngine>();
        services.AddSingleton<IServerDiscoveryService>(new QaDiscoveryService([]));
        services.AddSingleton<INotificationSettingsService, QaNotificationSettingsService>();

        var stateStore = new ServerMonitoringStateStore();
        stateStore.Set(new ServerMonitoringState
        {
            ServerId = QaNotificationServerService.ServerId,
            Health = ServerHealth.Healthy,
            LastSuccessAt = DateTimeOffset.UtcNow,
            LastAttemptAt = DateTimeOffset.UtcNow
        });
        services.AddSingleton<IServerMonitoringStateStore>(stateStore);
        services.AddSingleton<QaNotificationSequenceService>();
    }
}
