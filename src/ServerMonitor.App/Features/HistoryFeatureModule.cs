using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServerMonitor.App.Services;
using ServerMonitor.Core.History;
using ServerMonitor.Features;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.App.Features;

/// <summary>
/// M10 local history: recorder (an <c>IMonitoringCycleObserver</c>) → bounded channel → single writer →
/// SQLite. A database failure never blocks monitoring (ADR-015).
/// <para>
/// These registrations were previously written inline in the composition root's non-QA branch and are
/// moved here verbatim. The inert defaults they override (<see cref="NullServerHistoryQueryService"/> and
/// <c>NullHistoryMaintenanceService</c>) stay registered earlier in the root, so a composition without this
/// module still resolves the History UI and shows it as unavailable.
/// </para>
/// </summary>
public sealed class HistoryFeatureModule : IFeatureModule
{
    public FeatureDescriptor Descriptor => CommunityFeatures.History;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton(HistoryStorageOptions.ForCurrentUser());
        services.AddSingleton<SqliteServerHistoryStore>();
        services.AddSingleton<IServerHistoryStore>(sp => sp.GetRequiredService<SqliteServerHistoryStore>());
        services.AddSingleton<HistorySampleChannel>();
        services.AddSingleton<HistoryRecorder>();
        services.AddSingleton<HistoryWriterService>();
        services.AddSingleton<IServerHistoryQueryService>(sp => new ServerHistoryQueryService(
            sp.GetRequiredService<IServerHistoryStore>(),
            sp.GetRequiredService<ILogger<ServerHistoryQueryService>>()));
        services.AddSingleton<IHistoryMaintenanceService, HistoryMaintenanceService>();
    }
}
