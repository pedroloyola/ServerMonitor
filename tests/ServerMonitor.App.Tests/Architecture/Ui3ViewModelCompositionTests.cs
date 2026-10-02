using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Tests.TestSupport;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// Cortex R1 L-5: the transient UI.3 view models resolve from the REAL production composition with their new
/// dependencies (IServerService for the History selector, IServerMetricsStore for the Workloads context), over the
/// isolated composition (temp roots, structural real-data guard). Only the localization service is swapped (the WinRT
/// resource loader needs an app package); the pages themselves need XAML and are covered by the UIA pass.
/// </summary>
public sealed class Ui3ViewModelCompositionTests
{
    [Fact]
    public void HistoryAndWorkloadsViewModels_ResolveFromTheProductionComposition_AsTransients()
    {
        using var composition = new IsolatedAppComposition();
        composition.Services.AddSingleton<ILocalizationService>(new FakeLocalizationService());
        using var provider = composition.BuildProvider();

        using var history = provider.GetRequiredService<HistoryViewModel>();
        using var historyAgain = provider.GetRequiredService<HistoryViewModel>();
        using var workloads = provider.GetRequiredService<WorkloadsViewModel>();
        using var workloadsAgain = provider.GetRequiredService<WorkloadsViewModel>();

        Assert.NotSame(history, historyAgain);       // per navigation: a fresh VM, disposed on Unloaded
        Assert.NotSame(workloads, workloadsAgain);
        Assert.Empty(history.Servers);               // nothing loaded until the page calls Load
        Assert.Equal(string.Empty, workloads.SearchText);
    }

    [Fact]
    public void TheNewDependencies_AreRegisteredInTheProductionComposition()
    {
        var descriptors = IsolatedAppComposition.ProductionDescriptors();

        Assert.Contains(descriptors, d => d.ServiceType == typeof(Core.Interfaces.IServerService));
        Assert.Contains(descriptors, d => d.ServiceType == typeof(IServerMetricsStore));
        Assert.Contains(descriptors, d => d.ServiceType == typeof(HistoryViewModel) && d.Lifetime == ServiceLifetime.Transient);
        Assert.Contains(descriptors, d => d.ServiceType == typeof(WorkloadsViewModel) && d.Lifetime == ServiceLifetime.Transient);
    }
}
