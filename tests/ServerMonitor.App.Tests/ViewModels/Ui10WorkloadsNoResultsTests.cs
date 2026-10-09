using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.10 F12 (H06) and F14: the Workloads "no results" panel says which query emptied the page and its CTA undoes exactly
/// that (Prism UI.10A §H06; Figma A 112:15289, B 243:422, C 243:641), and it never hides a section that is not a list
/// (loading / error / not installed) - that section keeps its own card.
/// </summary>
public sealed class Ui10WorkloadsNoResultsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CaseA_SearchOnly_SaysTheSearch_AndClearsOnlyTheSearch()
    {
        using var vm = Load(HealthyDocker(), HealthyServices());

        vm.SearchText = "zzz";

        Assert.True(vm.ShowGlobalNoResults);
        Assert.Equal(WorkloadNoResultsCase.Search, vm.NoResultsCase);
        Assert.False(vm.IsNoResultsAllClear);
        Assert.Equal("Nenhum resultado para “zzz”", vm.NoResultsTitle);
        Assert.Equal("Experimenta outro nome.", vm.NoResultsMessage);
        Assert.Equal("Limpar pesquisa", vm.NoResultsActionLabel);

        vm.NoResultsActionCommand.Execute(null);

        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Equal(WorkloadGlobalFilter.All, vm.GlobalFilter);
        Assert.True(vm.ShowSectionCards);
    }

    [Fact]
    public void CaseB_FilterOnly_IsAPositiveState_AndShowsAll()
    {
        using var vm = Load(HealthyDocker(), HealthyServices());

        vm.GlobalFilter = WorkloadGlobalFilter.Problems;

        Assert.True(vm.ShowGlobalNoResults);
        Assert.Equal(WorkloadNoResultsCase.Filter, vm.NoResultsCase);
        Assert.True(vm.IsNoResultsAllClear); // tick, not the search glass
        Assert.Equal("Nenhum problema encontrado", vm.NoResultsTitle);
        Assert.Equal("Todos os serviços e containers estão a funcionar.", vm.NoResultsMessage);
        Assert.Equal("Mostrar todos", vm.NoResultsActionLabel);
        Assert.DoesNotContain("pesquisa", vm.NoResultsMessage + vm.NoResultsActionLabel, StringComparison.OrdinalIgnoreCase);

        vm.NoResultsActionCommand.Execute(null);

        Assert.Equal(WorkloadGlobalFilter.All, vm.GlobalFilter);
        Assert.True(vm.ShowSectionCards);
    }

    [Fact]
    public void CaseC_SearchAndFilter_NamesBoth_AndClearsBoth()
    {
        using var vm = Load(HealthyDocker(), HealthyServices());

        vm.SearchText = "redis";
        vm.GlobalFilter = WorkloadGlobalFilter.Problems;

        Assert.True(vm.ShowGlobalNoResults);
        Assert.Equal(WorkloadNoResultsCase.SearchAndFilter, vm.NoResultsCase);
        Assert.False(vm.IsNoResultsAllClear);
        Assert.Equal("Nenhum resultado para “redis” em Com problemas", vm.NoResultsTitle);
        Assert.Equal("Experimenta outro nome ou mostra todos.", vm.NoResultsMessage);
        Assert.Equal("Limpar pesquisa e filtro", vm.NoResultsActionLabel);

        vm.NoResultsActionCommand.Execute(null);

        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Equal(WorkloadGlobalFilter.All, vm.GlobalFilter);
        Assert.True(vm.ShowSectionCards);
    }

    [Theory]
    [InlineData("pt-BR", "Nenhum resultado para “redis” em Com problemas", "Tente outro nome ou mostre todos.", "Limpar pesquisa e filtro")]
    [InlineData("en-US", "No results for “redis” in With issues", "Try another name or show all.", "Clear search and filter")]
    public void CaseC_IsLocalised(string culture, string title, string message, string action)
    {
        using var vm = Load(HealthyDocker(), HealthyServices(), culture);
        vm.SearchText = "redis";
        vm.GlobalFilter = WorkloadGlobalFilter.Problems;

        Assert.Equal(title, vm.NoResultsTitle);
        Assert.Equal(message, vm.NoResultsMessage);
        Assert.Equal(action, vm.NoResultsActionLabel);
    }

    [Theory]
    [InlineData(DockerAvailability.NotInstalled)]
    [InlineData(DockerAvailability.PermissionDenied)]
    [InlineData(DockerAvailability.Unknown)]
    public void F14_ADockerStateThatIsNotAList_IsNeverHiddenBehindTheGlobalNoResults(DockerAvailability docker)
    {
        using var vm = Load(new DockerSnapshot { Availability = docker }, HealthyServices());

        vm.SearchText = "zzz";

        Assert.False(vm.ShowGlobalNoResults);
        Assert.True(vm.ShowSectionCards);           // the Docker card keeps saying what is wrong
        Assert.True(vm.ShowServicesSectionNoResults); // the services card says its own "no results"
    }

    /// <summary>
    /// The F14 mirror (tests review M-1): Docker is a healthy list but Services is not a list (unavailable, denied,
    /// unsupported, probe failed) - a search that matches nothing must keep the Services state card on screen, never hide
    /// it behind the page-wide "no results".
    /// </summary>
    [Theory]
    [InlineData(ServiceManager.Systemd, WorkloadServiceAvailability.Unavailable)]
    [InlineData(ServiceManager.Systemd, WorkloadServiceAvailability.PermissionDenied)]
    [InlineData(ServiceManager.Unsupported, WorkloadServiceAvailability.Available)]
    [InlineData(ServiceManager.Systemd, WorkloadServiceAvailability.Unknown)]
    public void F14_AServicesStateThatIsNotAList_IsNeverHiddenBehindTheGlobalNoResults(ServiceManager manager, WorkloadServiceAvailability services)
    {
        using var vm = Load(HealthyDocker(), new ServiceSnapshot { Manager = manager, Availability = services });
        Assert.False(vm.ShowServicesList);

        vm.SearchText = "zzz";

        Assert.False(vm.ShowGlobalNoResults);
        Assert.True(vm.ShowSectionCards);          // the Services card keeps saying what is wrong
        Assert.True(vm.ShowDockerSectionNoResults); // the Docker card says its own "no results"
        Assert.True(vm.ShowServicesUnavailable || vm.ShowServicesUnsupported || vm.ShowServicesError);
    }

    [Fact]
    public void F14_WhileTheSectionsAreLoadingAgain_TheGlobalNoResultsWaits_AndSaysSo()
    {
        var store = new InMemoryServerWorkloadStore();
        using var vm = Load(HealthyDocker(), HealthyServices(), store: store);
        vm.SearchText = "zzz";
        Assert.True(vm.ShowGlobalNoResults);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        // A snapshot with no completed attempt puts both sections back in Loading.
        store.Set(new ServerWorkloadSnapshot { ServerId = Id, CapturedAtUtc = Now, Docker = HealthyDocker(), Services = HealthyServices() });

        Assert.True(vm.ShowPageLoading);
        Assert.False(vm.ShowGlobalNoResults);
        Assert.Contains(nameof(WorkloadsViewModel.ShowGlobalNoResults), raised);
    }

    private static readonly Guid Id = Guid.Parse("7f1c0a52-2a8e-4d55-9a3b-4d1f3c2f6a10");

    private static DockerSnapshot HealthyDocker() => new()
    {
        Availability = DockerAvailability.Available,
        Containers = [new ContainerInfo { ContainerId = "a", Name = "nginx", Image = "nginx", State = ContainerState.Running, StatusText = "Up", Health = ContainerHealth.None }]
    };

    private static ServiceSnapshot HealthyServices() => new()
    {
        Manager = ServiceManager.Systemd,
        Availability = WorkloadServiceAvailability.Available,
        Services = [new ServiceInfo { Id = "ssh.service", Name = "ssh", State = ServiceState.Running }]
    };

    private static WorkloadsViewModel Load(DockerSnapshot docker, ServiceSnapshot services, string culture = "pt-PT",
        InMemoryServerWorkloadStore? store = null)
    {
        store ??= new InMemoryServerWorkloadStore();
        store.Set(new ServerWorkloadSnapshot { ServerId = Id, CapturedAtUtc = Now, LastAttemptAtUtc = Now, Docker = docker, Services = services });
        var vm = new WorkloadsViewModel(store, new NoOp(), new FakeServerMetricsStore(), new FakeNavigationService(),
            new ResWLocalizationService(culture), NullLogger<WorkloadsViewModel>.Instance, new FakeTimeProvider(Now));
        vm.Load(Id, "web-01");
        return vm;
    }

    private sealed class NoOp : IWorkloadRefreshCoordinator
    {
        public Task RefreshNowAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
