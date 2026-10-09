using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>UI.3 phase 1: Workloads global search/filter, summaries, display copy and context (real resw copy).</summary>
public sealed class WorkloadsViewModelUi3Tests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid ServerId = Guid.NewGuid();

    private sealed class NoOpCoordinator : IWorkloadRefreshCoordinator
    {
        public Task RefreshNowAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static ContainerInfo Container(string name, string image, ContainerState state, ContainerHealth health) => new()
    {
        ContainerId = name.PadRight(12, '0'),
        Name = name,
        Image = image,
        State = state,
        StatusText = state.ToString(),
        Health = health
    };

    private static ServiceInfo Service(string name, string? description, ServiceState state, ServiceStartupState? startup) => new()
    {
        Id = name + ".service",
        Name = name,
        DisplayName = description,
        State = state,
        StartupState = startup
    };

    /// <summary>The Figma frame's data: 6 containers (1 unhealthy, 1 stopped) and 6 services (1 failed).</summary>
    private static IReadOnlyList<ContainerInfo> FigmaContainers() =>
    [
        Container("web", "nginx:1.27", ContainerState.Running, ContainerHealth.Healthy),
        Container("api", "example/api:2.4", ContainerState.Running, ContainerHealth.None),
        Container("worker", "example/worker:2.4", ContainerState.Running, ContainerHealth.Unhealthy),
        Container("redis", "redis:7", ContainerState.Running, ContainerHealth.Healthy),
        Container("postgres", "postgres:16", ContainerState.Running, ContainerHealth.Healthy),
        Container("backup-job", "example/backup:1", ContainerState.Exited, ContainerHealth.None)
    ];

    private static IReadOnlyList<ServiceInfo> FigmaServices() =>
    [
        Service("nginx", "A high performance web server", ServiceState.Running, ServiceStartupState.Enabled),
        Service("ssh", "OpenBSD Secure Shell server", ServiceState.Running, ServiceStartupState.Enabled),
        Service("cron", "Regular background program processing daemon", ServiceState.Running, ServiceStartupState.Enabled),
        Service("docker", "Docker Application Container Engine", ServiceState.Running, ServiceStartupState.Enabled),
        Service("systemd-journald", "Journal Service", ServiceState.Running, ServiceStartupState.Static),
        Service("backup", "Nightly backup", ServiceState.Failed, ServiceStartupState.Enabled)
    ];

    private static ServerWorkloadSnapshot Snapshot(
        IReadOnlyList<ContainerInfo>? containers = null,
        IReadOnlyList<ServiceInfo>? services = null,
        DockerAvailability docker = DockerAvailability.Available,
        WorkloadServiceAvailability serviceAvailability = WorkloadServiceAvailability.Available,
        ServiceManager manager = ServiceManager.Systemd,
        bool stale = false,
        DateTimeOffset? capturedAt = null) => new()
    {
        ServerId = ServerId,
        CapturedAtUtc = capturedAt ?? Now,
        LastAttemptAtUtc = Now,
        IsStale = stale,
        Docker = new DockerSnapshot { Availability = docker, Containers = containers ?? FigmaContainers() },
        Services = new ServiceSnapshot
        {
            Manager = manager,
            Availability = serviceAvailability,
            Services = services ?? FigmaServices()
        }
    };

    /// <summary>UI.4 (Boss, Beacon r1 SHOULD-4): the server crumb leads to that server's interim page, not the Dashboard.</summary>
    [Fact]
    public void Back_ReturnsToTheServersInterimPage()
    {
        var navigation = new FakeNavigationService();
        using var vm = new WorkloadsViewModel(new InMemoryServerWorkloadStore(), new NoOpCoordinator(), new FakeServerMetricsStore(), navigation,
            new ResWLocalizationService("pt-PT"), NullLogger<WorkloadsViewModel>.Instance, new FakeTimeProvider(Now));
        vm.Load(ServerId, "prod-web-01");

        vm.BackCommand.Execute(null);

        Assert.Equal([ServerId], navigation.ServerDetailReturns);
        Assert.Equal(0, navigation.DashboardCount);
    }

    private static (WorkloadsViewModel Vm, InMemoryServerWorkloadStore Store, FakeServerMetricsStore Metrics) New(
        ServerWorkloadSnapshot? snapshot,
        string culture = "pt-PT",
        ServerMetricsSnapshot? metrics = null)
    {
        var store = new InMemoryServerWorkloadStore();
        if (snapshot is not null)
        {
            store.Set(snapshot);
        }

        var metricsStore = new FakeServerMetricsStore { InitialSnapshot = metrics };
        var vm = new WorkloadsViewModel(
            store,
            new NoOpCoordinator(),
            metricsStore,
            new FakeNavigationService(),
            new ResWLocalizationService(culture),
            NullLogger<WorkloadsViewModel>.Instance,
            new FakeTimeProvider(Now));
        vm.Load(ServerId, "prod-web-01");
        return (vm, store, metricsStore);
    }

    private static string[] Names(WorkloadsViewModel vm) =>
        vm.Containers.Select(c => c.Name).Concat(vm.Services.Select(s => s.Name)).ToArray();

    // --- Global search --------------------------------------------------------------------------------------

    [Fact]
    public void Search_IsCaseInsensitive_AndAppliesToBothLists()
    {
        var (vm, _, _) = New(Snapshot());

        vm.SearchText = "NGINX";

        // Container matched by image ("nginx:1.27"), service by name.
        Assert.Equal(new[] { "web" }, vm.Containers.Select(c => c.Name));
        Assert.Equal(new[] { "nginx" }, vm.Services.Select(s => s.Name));
        Assert.False(vm.ShowGlobalNoResults);
    }

    [Fact]
    public void Search_MatchesServiceDescription()
    {
        var (vm, _, _) = New(Snapshot());

        vm.SearchText = "secure shell";

        Assert.Empty(vm.Containers);
        Assert.Equal(new[] { "ssh" }, vm.Services.Select(s => s.Name));
    }

    [Fact]
    public void Search_OnlyContainersMatch_FlagsTheServicesSectionOnly()
    {
        var (vm, _, _) = New(Snapshot());

        vm.SearchText = "redis";

        Assert.Equal(new[] { "redis" }, vm.Containers.Select(c => c.Name));
        Assert.Empty(vm.Services);
        Assert.False(vm.ShowGlobalNoResults);
        Assert.False(vm.ShowDockerSectionNoResults);
        Assert.True(vm.ShowServicesSectionNoResults);
    }

    [Fact]
    public void Search_OnlyServicesMatch_FlagsTheContainersSectionOnly()
    {
        var (vm, _, _) = New(Snapshot());

        vm.SearchText = "journald";

        Assert.Empty(vm.Containers);
        Assert.Equal(new[] { "systemd-journald" }, vm.Services.Select(s => s.Name));
        Assert.True(vm.ShowDockerSectionNoResults);
        Assert.False(vm.ShowServicesSectionNoResults);
        Assert.False(vm.ShowGlobalNoResults);
    }

    [Fact]
    public void Search_Mixed_MatchesInBothSections()
    {
        var (vm, _, _) = New(Snapshot());

        vm.SearchText = "  Backup ";

        Assert.Equal(new[] { "backup-job", "backup" }, Names(vm));
    }

    [Fact]
    public void Search_ZeroResults_IsAGlobalMiss_WithTheQueryInTheTitle()
    {
        var (vm, _, _) = New(Snapshot());

        vm.SearchText = "zzz";

        Assert.Empty(Names(vm));
        Assert.True(vm.HasActiveQuery);
        Assert.True(vm.ShowGlobalNoResults);
        Assert.False(vm.ShowDockerSectionNoResults);
        Assert.False(vm.ShowServicesSectionNoResults);
        Assert.Equal("Nenhum resultado para “zzz”", vm.NoResultsTitle);
    }

    [Fact]
    public void Search_WithDockerUnavailable_AppliesToServices_AndKeepsDockerState()
    {
        var (vm, _, _) = New(Snapshot(containers: [], docker: DockerAvailability.NotInstalled));

        vm.SearchText = "nginx";
        Assert.Equal(new[] { "nginx" }, vm.Services.Select(s => s.Name));
        Assert.True(vm.ShowDockerNotInstalled);
        Assert.False(vm.ShowGlobalNoResults);

        // UI.10 F14 (Prism UI.10A, accepted): Docker is not a list here, so the page-wide "no results" would hide its
        // "not installed" card - the cards stay, Docker says what it is, the services card says its own "no results".
        vm.SearchText = "zzz";
        Assert.False(vm.ShowGlobalNoResults);
        Assert.True(vm.ShowSectionCards);
        Assert.True(vm.ShowDockerNotInstalled);
        Assert.True(vm.ShowServicesSectionNoResults);
    }

    [Fact]
    public void Search_BothSectionsUnavailable_NeverShowsAFakeNoResults()
    {
        var (vm, _, _) = New(Snapshot(
            containers: [],
            services: [],
            docker: DockerAvailability.Unknown,
            serviceAvailability: WorkloadServiceAvailability.Unknown));

        vm.SearchText = "nginx";

        Assert.True(vm.ShowAllUnavailable);
        Assert.False(vm.ShowGlobalNoResults);
    }

    [Fact]
    public void Search_IsCultureInvariant_UnderTurkishCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.True(WorkloadPresentation.Matches("INFLUX", "influxdb"));
            Assert.True(WorkloadPresentation.Matches("", "anything"));
            Assert.False(WorkloadPresentation.Matches("x", null, ""));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    // --- Global filter + counts (D-UI3-3) -------------------------------------------------------------------

    [Fact]
    public void Counts_MatchTheFigmaFrame()
    {
        var (vm, _, _) = New(Snapshot());

        Assert.True(vm.HasFilterCounts);
        Assert.Equal(12, vm.AllCount);
        Assert.Equal(2, vm.ProblemCount);
        Assert.Equal("Todos · 12", vm.FilterAllLabel);
        Assert.Equal("Com problemas · 2", vm.FilterProblemsLabel);
    }

    [Fact]
    public void ProblemsFilter_KeepsNegativeOnly_InBothLists()
    {
        var (vm, _, _) = New(Snapshot());

        vm.GlobalFilterIndex = 1;

        Assert.Equal(WorkloadGlobalFilter.Problems, vm.GlobalFilter);
        Assert.Equal(new[] { "worker" }, vm.Containers.Select(c => c.Name));
        Assert.Equal(new[] { "backup" }, vm.Services.Select(s => s.Name));
        Assert.True(vm.HasActiveQuery);
    }

    [Fact]
    public void ProblemsFilter_ExcludesTransientWarnings()
    {
        var (vm, _, _) = New(Snapshot(
            containers:
            [
                Container("booting", "example/a", ContainerState.Running, ContainerHealth.Starting),
                Container("bouncing", "example/b", ContainerState.Restarting, ContainerHealth.None),
                Container("dead", "example/c", ContainerState.Dead, ContainerHealth.None)
            ],
            services:
            [
                Service("starting", null, ServiceState.Starting, null),
                Service("stopping", null, ServiceState.Stopping, null),
                Service("stopped", null, ServiceState.Stopped, ServiceStartupState.Disabled)
            ]));

        Assert.Equal(1, vm.ProblemCount);
        vm.GlobalFilter = WorkloadGlobalFilter.Problems;

        Assert.Equal(new[] { "dead" }, Names(vm));
    }

    [Fact]
    public void ProblemsFilter_WithNoProblems_IsAGlobalMiss_WithTheFilterTitle()
    {
        var (vm, _, _) = New(Snapshot(
            containers: [Container("web", "nginx", ContainerState.Running, ContainerHealth.Healthy)],
            services: [Service("ssh", null, ServiceState.Running, null)]));

        vm.GlobalFilter = WorkloadGlobalFilter.Problems;

        Assert.Equal("Com problemas · 0", vm.FilterProblemsLabel);
        Assert.True(vm.ShowGlobalNoResults);
        Assert.Equal("Nenhum problema encontrado", vm.NoResultsTitle);
    }

    [Fact]
    public void FilterAndSearch_Combine()
    {
        var (vm, _, _) = New(Snapshot());

        vm.GlobalFilter = WorkloadGlobalFilter.Problems;
        vm.SearchText = "worker";

        Assert.Equal(new[] { "worker" }, Names(vm));
    }

    [Fact]
    public void NoResultsAction_ForSearchAndFilter_ClearsTheText_AndResetsTheFilterToAll()
    {
        // UI.10 F12 case C: the CTA says "Limpar pesquisa e filtro" and does exactly that (cases A/B: Ui10WorkloadsNoResultsTests).
        var (vm, _, _) = New(Snapshot());
        vm.SearchText = "zzz";
        vm.GlobalFilter = WorkloadGlobalFilter.Problems;
        Assert.Equal(WorkloadNoResultsCase.SearchAndFilter, vm.NoResultsCase);

        vm.NoResultsActionCommand.Execute(null);

        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Equal(WorkloadGlobalFilter.All, vm.GlobalFilter);
        Assert.False(vm.HasActiveQuery);
        Assert.False(vm.ShowGlobalNoResults);
        Assert.Equal(12, Names(vm).Length);
    }

    [Fact]
    public void Loading_HasNoCounts()
    {
        var (vm, _, _) = New(snapshot: null);

        Assert.False(vm.HasFilterCounts);
        Assert.Equal("Todos", vm.FilterAllLabel);
        Assert.Equal("Com problemas", vm.FilterProblemsLabel);
        Assert.True(vm.ShowDockerLoading);
        Assert.True(vm.ShowServicesLoading);
    }

    [Fact]
    public void FilterCounts_SurviveAFreshSnapshot()
    {
        var (vm, store, _) = New(Snapshot());
        vm.GlobalFilter = WorkloadGlobalFilter.Problems;

        store.Set(Snapshot(services: [Service("a", null, ServiceState.Failed, null), Service("b", null, ServiceState.Failed, null)]));

        Assert.Equal(3, vm.ProblemCount);
        Assert.Equal(new[] { "worker", "a", "b" }, Names(vm));
    }

    // --- Summaries + badges -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("pt-PT", "Docker · 5 em execução · 1 parado", "systemd · 5 ativos · 1 falha", "1 problema")]
    [InlineData("pt-BR", "Docker · 5 em execução · 1 parado", "systemd · 5 ativos · 1 falha", "1 problema")]
    [InlineData("en-US", "Docker · 5 running · 1 stopped", "systemd · 5 active · 1 failed", "1 issue")]
    public void LifecycleSummaries_AndBadges_MatchTheFigmaFrame(string culture, string docker, string services, string badge)
    {
        var (vm, _, _) = New(Snapshot(), culture);

        Assert.Equal(docker, vm.DockerLifecycleSummary);
        Assert.Equal(services, vm.ServicesLifecycleSummary);
        Assert.Equal(badge, vm.DockerProblemBadge);
        Assert.Equal(badge, vm.ServicesProblemBadge);
        Assert.True(vm.HasDockerProblems);
    }

    [Theory]
    [InlineData("pt-PT", "2 problemas", "Docker · 2 em execução · 1 em pausa · 2 parados")]
    [InlineData("pt-BR", "2 problemas", "Docker · 2 em execução · 1 pausado · 2 parados")]
    [InlineData("en-US", "2 issues", "Docker · 2 running · 1 paused · 2 stopped")]
    public void Plurals_AreCorrectInEveryCulture(string culture, string badge, string summary)
    {
        var (vm, _, _) = New(Snapshot(containers:
        [
            Container("a", "i", ContainerState.Running, ContainerHealth.Unhealthy),
            Container("b", "i", ContainerState.Running, ContainerHealth.Unhealthy),
            Container("c", "i", ContainerState.Exited, ContainerHealth.None),
            Container("d", "i", ContainerState.Created, ContainerHealth.None),
            Container("e", "i", ContainerState.Paused, ContainerHealth.None)
        ]), culture);

        Assert.Equal(badge, vm.DockerProblemBadge);
        Assert.Equal(summary, vm.DockerLifecycleSummary);
    }

    [Fact]
    public void NoProblems_NoBadge_AndFailuresAreNeverLastInTheSummary()
    {
        var (vm, _, _) = New(Snapshot(
            containers: [Container("web", "nginx", ContainerState.Running, ContainerHealth.Healthy)],
            services:
            [
                Service("a", null, ServiceState.Stopped, null),
                Service("b", null, ServiceState.Failed, null),
                Service("c", null, ServiceState.Failed, null),
                Service("d", null, ServiceState.Running, null)
            ],
            manager: ServiceManager.Launchd));

        Assert.Null(vm.DockerProblemBadge);
        Assert.False(vm.HasDockerProblems);
        Assert.Equal("launchd · 1 ativo · 2 falhas · 1 inativo", vm.ServicesLifecycleSummary);
        Assert.Equal("2 problemas", vm.ServicesProblemBadge);
    }

    [Fact]
    public void Summaries_AreEmptyWithoutAList()
    {
        var (vm, _, _) = New(Snapshot(containers: [], docker: DockerAvailability.PermissionDenied));

        Assert.Equal(string.Empty, vm.DockerLifecycleSummary);
        Assert.Null(vm.DockerProblemBadge);
        Assert.NotEqual(string.Empty, vm.ServicesLifecycleSummary);
    }

    // --- Row display copy ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ContainerState.Running, ContainerHealth.None, "Sem verificação")]
    [InlineData(ContainerState.Running, ContainerHealth.Unknown, "—")]
    [InlineData(ContainerState.Running, ContainerHealth.Healthy, "Saudável")]
    [InlineData(ContainerState.Running, ContainerHealth.Unhealthy, "Não saudável")]
    [InlineData(ContainerState.Running, ContainerHealth.Starting, "A iniciar")]
    [InlineData(ContainerState.Exited, ContainerHealth.Healthy, "—")]
    [InlineData(ContainerState.Exited, ContainerHealth.None, "—")]
    [InlineData(ContainerState.Restarting, ContainerHealth.Starting, "A iniciar")]
    public void ContainerHealthDisplay(ContainerState state, ContainerHealth health, string expected)
    {
        var row = new ContainerRowViewModel(Container("x", "i", state, health), new ResWLocalizationService("pt-PT"));

        Assert.Equal(expected, row.HealthDisplay);
        Assert.DoesNotContain("—", row.DisplayAutomationName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ServiceState.Running, "Ativo")]
    [InlineData(ServiceState.Stopped, "Inativo")]
    [InlineData(ServiceState.Failed, "Falhou")]
    [InlineData(ServiceState.Starting, "A iniciar")]
    public void ServiceStateDisplay(ServiceState state, string expected)
    {
        var row = new ServiceRowViewModel(Service("x", null, state, null), new ResWLocalizationService("pt-PT"));

        Assert.Equal(expected, row.StateDisplay);
    }

    [Theory]
    [InlineData("pt-PT", ServiceStartupState.Enabled, "Automático")]
    [InlineData("pt-PT", ServiceStartupState.Static, "Estático")]
    [InlineData("pt-PT", ServiceStartupState.Disabled, "Manual")]
    [InlineData("pt-PT", ServiceStartupState.Masked, "Bloqueado")]
    [InlineData("pt-PT", ServiceStartupState.Unknown, "—")]
    [InlineData("pt-PT", null, "—")]
    [InlineData("pt-BR", ServiceStartupState.Enabled, "Automático")]
    [InlineData("en-US", ServiceStartupState.Enabled, "Automatic")]
    [InlineData("en-US", ServiceStartupState.Masked, "Blocked")]
    public void ServiceStartupDisplay(string culture, ServiceStartupState? startup, string expected)
    {
        var row = new ServiceRowViewModel(Service("x", null, ServiceState.Running, startup), new ResWLocalizationService(culture));

        Assert.Equal(expected, row.StartupDisplay);
        Assert.DoesNotContain("—", row.DisplayAutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainersKeepEmExecucao_ServicesSayAtivo()
    {
        var (vm, _, _) = New(Snapshot());

        Assert.Equal("Em execução", vm.Containers.First(c => c.Name == "web").StateText);
        Assert.Equal("Ativo", vm.Services.First(s => s.Name == "nginx").StateDisplay);
    }

    // --- Header context (D-UI3-9) -----------------------------------------------------------------------------

    [Fact]
    public void Context_ServerOsAndMinutes()
    {
        var (vm, _, _) = New(
            Snapshot(capturedAt: Now.AddMinutes(-3)),
            metrics: new ServerMetricsSnapshot
            {
                ServerId = ServerId,
                CollectedAt = Now,
                OperatingSystemName = "Ubuntu 24.04 LTS",
                OperatingSystemVersion = "24.04"
            });

        Assert.Equal("prod-web-01 · Ubuntu 24.04 LTS · Atualizado há 3 min", vm.ContextDisplay);
    }

    // --- "Atualizado há" with seconds, no timer (D-UI3-9, final fidelity) --------------------------------------

    [Theory]
    [InlineData(0, "Atualizado agora mesmo")]
    [InlineData(900, "Atualizado agora mesmo")]
    [InlineData(1_000, "Atualizado há 1 s")]
    [InlineData(8_000, "Atualizado há 8 s")]
    [InlineData(59_900, "Atualizado há 59 s")]
    [InlineData(60_000, "Atualizado há 1 min")]
    [InlineData(59 * 60_000, "Atualizado há 59 min")]
    [InlineData(60 * 60_000, "Atualizado há 1 h")]
    [InlineData(24 * 60 * 60_000, "Atualizado há 1 d")]
    [InlineData(-5_000, "Atualizado agora mesmo")]     // clock skew: a capture "in the future" never reads as negative
    public void UpdatedAgo_SecondsUnderAMinute_ThenMinutesHoursDays(long ageMilliseconds, string expected)
    {
        var (vm, _, _) = New(Snapshot(capturedAt: Now.AddMilliseconds(-ageMilliseconds)));

        Assert.Equal(expected, vm.UpdatedAgoDisplay);
        Assert.Equal($"prod-web-01 · {expected}", vm.ContextDisplay);
    }

    [Theory]
    [InlineData("pt-PT", "Atualizado há 8 s")]
    [InlineData("pt-BR", "Atualizado há 8 s")]
    [InlineData("en-US", "Updated 8 s ago")]
    public void UpdatedAgo_Seconds_IsLocalized(string culture, string expected)
    {
        var (vm, _, _) = New(Snapshot(capturedAt: Now.AddSeconds(-8)), culture);

        Assert.Equal(expected, vm.UpdatedAgoDisplay);
    }

    [Fact]
    public void UpdatedAgo_HasNoTimer_TheTextOnlyChangesOnANewApply()
    {
        var store = new InMemoryServerWorkloadStore();
        store.Set(Snapshot(capturedAt: Now.AddSeconds(-8)));
        var time = new FakeTimeProvider(Now);
        using var vm = new WorkloadsViewModel(store, new NoOpCoordinator(), new FakeServerMetricsStore(), new FakeNavigationService(),
            new ResWLocalizationService("pt-PT"), NullLogger<WorkloadsViewModel>.Instance, time);
        vm.Load(ServerId, "prod-web-01");
        var changes = 0;
        vm.PropertyChanged += (_, e) => changes += e.PropertyName is nameof(WorkloadsViewModel.UpdatedAgoDisplay) or nameof(WorkloadsViewModel.ContextDisplay) ? 1 : 0;

        // Time passes (FakeTimeProvider fires any timer created through it); nothing re-renders the label.
        time.Advance(TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("Atualizado há 8 s", vm.UpdatedAgoDisplay);
        Assert.Equal(0, changes);

        // A new snapshot (store change -> Apply) is what recomputes it.
        store.Set(Snapshot(capturedAt: Now.AddSeconds(-8)));
        Assert.Equal("Atualizado há 5 min", vm.UpdatedAgoDisplay);

        // And the view model holds no timer of its own (source guard: no tick-driven label).
        var code = ServerMonitor.App.Tests.Architecture.AppSourceTree.CodeWithoutComments("ViewModels/WorkloadsViewModel.cs");
        Assert.DoesNotContain("DispatcherTimer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherQueueTimer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("PeriodicTimer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTimer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Threading.Timer", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_UnknownOs_IsOmitted()
    {
        var (vm, _, _) = New(Snapshot());

        Assert.Equal("prod-web-01 · Atualizado agora mesmo", vm.ContextDisplay);
    }

    [Fact]
    public void Context_Stale_SaysTheLastQueryFailed_AndKeepsTheList()
    {
        var (vm, _, _) = New(Snapshot(stale: true, capturedAt: Now.AddMinutes(-12)));

        Assert.Equal("prod-web-01 · Atualizado há 12 min · Falha na última consulta", vm.ContextDisplay);
        Assert.True(vm.ShowStaleNotice);
        Assert.Equal(12, vm.AllCount);
    }

    [Fact]
    public void Context_Loading()
    {
        var (vm, _, _) = New(snapshot: null);

        Assert.Equal("prod-web-01 · A consultar serviços e containers…", vm.ContextDisplay);
    }

    [Fact]
    public void Context_AllUnavailable()
    {
        var (vm, _, _) = New(Snapshot(
            containers: [],
            services: [],
            docker: DockerAvailability.Unknown,
            serviceAvailability: WorkloadServiceAvailability.Unknown));

        Assert.True(vm.ShowAllUnavailable);
        Assert.Equal("prod-web-01 · Falha na última consulta", vm.ContextDisplay);
    }

    [Fact]
    public void NothingToShow_OnlyWhenBothReadsSucceededEmpty()
    {
        var (vm, _, _) = New(Snapshot(containers: [], services: []));

        Assert.True(vm.ShowNothingToShow);
        Assert.Equal("Todos · 0", vm.FilterAllLabel);
        Assert.False(vm.ShowGlobalNoResults);
    }

    // --- Preserved behaviour -----------------------------------------------------------------------------------

    [Fact]
    public void SectionsStillFailIndependently()
    {
        var (vm, _, _) = New(Snapshot(containers: [], docker: DockerAvailability.PermissionDenied));

        Assert.True(vm.ShowDockerPermissionDenied);
        Assert.True(vm.ShowServicesList);
        Assert.Equal(6, vm.Services.Count);
        Assert.Equal(6, vm.AllCount);
        Assert.False(vm.ShowAllUnavailable);
    }

    [Fact]
    public void Truncated_And_Order_ArePreserved()
    {
        var snapshot = Snapshot() with
        {
            Docker = new DockerSnapshot { Availability = DockerAvailability.Available, Containers = FigmaContainers(), Truncated = true },
            Services = new ServiceSnapshot
            {
                Manager = ServiceManager.Systemd,
                Availability = WorkloadServiceAvailability.Available,
                Services = FigmaServices(),
                Truncated = true
            }
        };
        var (vm, _, _) = New(snapshot);

        Assert.True(vm.ShowDockerTruncatedNotice);
        Assert.True(vm.ShowServicesTruncatedNotice);
        Assert.Equal("backup-job", vm.Containers.Last().Name); // running first
        Assert.Equal("backup", vm.Services.First().Name);      // failed first
    }

    [Fact]
    public void Dispose_StopsReactingToTheStore()
    {
        var (vm, store, _) = New(Snapshot());

        vm.Dispose();
        store.Set(Snapshot(containers: [], services: []));

        Assert.Equal(12, vm.AllCount);
    }
}
