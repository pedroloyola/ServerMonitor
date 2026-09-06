using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.Collectors.Workloads;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Workloads;
using ServerMonitor.Features;
using ServerMonitor.Infrastructure.Collectors.Workloads;

namespace ServerMonitor.App.Features;

/// <summary>
/// M11 read-only workloads (Docker + services). The cadence observer rides the same cycle signal as
/// history; the collector service runs SSH off the engine thread with its own single-flight and
/// concurrency limit. The real <see cref="WorkloadCollector"/> maps the fixed read-only catalog over the
/// shared SSH session; a Debug <c>--qa-workloads</c> run replaces it with a deterministic fake, registered
/// later so it wins.
/// <para>
/// Moved verbatim from the composition root's non-QA branch. The inert defaults
/// (<see cref="InMemoryServerWorkloadStore"/>, <see cref="NullWorkloadRefreshCoordinator"/>) stay
/// registered earlier in the root.
/// </para>
/// </summary>
public sealed class WorkloadsFeatureModule : IFeatureModule
{
    public FeatureDescriptor Descriptor => CommunityFeatures.Workloads;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton(WorkloadOptions.Default);
        services.AddSingleton<IWorkloadCollector>(sp =>
            new WorkloadCollector(sp.GetRequiredService<IWorkloadRemoteSource>()));
        services.AddSingleton<WorkloadRequestQueue>();
        services.AddSingleton(sp => new WorkloadCadencePolicy(
            sp.GetRequiredService<WorkloadOptions>().MinCadence));
        services.AddSingleton<WorkloadCadenceObserver>();
        services.AddSingleton<WorkloadCollectorService>();
        services.AddSingleton<IWorkloadRefreshCoordinator>(sp =>
            sp.GetRequiredService<WorkloadCollectorService>());
    }
}
