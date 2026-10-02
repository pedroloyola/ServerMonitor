using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.TestSupport;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.4 composition (real root, isolated data roots): D-UI4-PRIORITY reads the very thresholds the engine derives health
/// from — one MonitoringOptions instance, not a second copy of the rule — and the new screens are per-visit (transient)
/// views over the singleton dashboard.
/// </summary>
public sealed class Ui4CompositionTests
{
    [Fact]
    public async Task TheEngineAndTheOverview_ShareOneMonitoringOptionsInstance()
    {
        using var composition = new IsolatedAppComposition();
        await using var provider = composition.BuildProvider();

        var options = provider.GetRequiredService<MonitoringOptions>();
        var engine = provider.GetRequiredService<MonitoringEngine>();
        var engineOptions = typeof(MonitoringEngine).GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine);

        Assert.Same(options, engineOptions);
        Assert.NotSame(MonitoringOptions.Default, options);
        Assert.Equal(MonitoringOptions.Default.Thresholds, options.Thresholds);

        var dashboard = provider.GetRequiredService<DashboardViewModel>();
        var selector = (PriorityProblemSelector)typeof(DashboardViewModel)
            .GetField("_prioritySelector", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dashboard)!;
        Assert.Same(options.Thresholds, selector.Thresholds);
    }

    [Fact]
    public void TheNewScreens_ArePerVisit()
    {
        var descriptors = IsolatedAppComposition.ProductionDescriptors();

        foreach (var type in new[] { typeof(ServersPage), typeof(ServersViewModel), typeof(ServerDetailPage), typeof(ServerDetailViewModel) })
        {
            Assert.Equal(ServiceLifetime.Transient, Assert.Single(descriptors, d => d.ServiceType == type).Lifetime);
        }

        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(descriptors, d => d.ServiceType == typeof(DashboardViewModel)).Lifetime);
    }
}
