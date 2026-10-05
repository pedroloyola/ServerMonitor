using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.TestSupport;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Domain;
namespace ServerMonitor.App.Tests.Architecture;
public sealed class Ui6CompositionTests
{
    [Fact]
    public async Task RealComposition_SharesActualServerDiagnosis()
    {
        using var composition = new IsolatedAppComposition();
        await using var provider = composition.BuildProvider();
        var servers = provider.GetRequiredService<IServerService>();
        Assert.IsType<ServerService>(servers);
        Assert.Same(servers, provider.GetRequiredService<IServerLoadStatusSource>());
    }
    [Fact]
    public async Task RealComposition_SharesExactDashboardActionsAndProcessSingletons()
    {
        using var composition = new IsolatedAppComposition();
        await using var provider = composition.BuildProvider();
        var dashboard = provider.GetRequiredService<DashboardViewModel>();
        var actions = provider.GetRequiredService<OnboardingActions>();
        Assert.Same(dashboard.AddServerCommand, actions.AddServerCommand);
        Assert.Same(dashboard.ImportFromSshCommand, actions.ImportFromSshCommand);
        Assert.Same(dashboard.RestoreHiddenServersCommand, dashboard.RestoreHiddenServersCommand);
        foreach (var type in new[] { typeof(OnboardingViewModel), typeof(ShellViewModel), typeof(ActivationLatch) })
            Assert.Same(provider.GetRequiredService(type), provider.GetRequiredService(type));
    }
}
