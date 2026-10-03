using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Windowing;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI.4 §6: the Debug-only Visão geral / Servidores harness (<c>--qa-overview</c> + <c>--qa-overview-scenario</c>). It is
/// in THE strict parser and the launcher, refuses an unknown scenario, registers only in-memory doubles, fixes only the
/// overview's clock, and its catalogue is synthetic, deterministic and consistent with the engine's health rule. Pure: no
/// launch, no real path, no credential, no network.
/// </summary>
public sealed class QaOverviewHarnessTests
{
    private const string Exe = @"C:\fixture\ServerMonitor.App.exe";

    public static TheoryData<string> Scenarios => new(QaOverviewScenarioPolicy.Scenarios);

    // ---- parser / launcher -------------------------------------------------------------------------------------

    [Fact]
    public void TheFlagAndTheModifier_AreInTheStrictParser()
    {
        Assert.Contains(QaOverviewComposition.LaunchFlag, QaStartupIsolation.HarnessFlags);
        Assert.Contains(QaOverviewScenarioPolicy.LaunchFlag, QaStartupIsolation.ModifierFlags);
        Assert.True(QaStartupIsolation.IsHarnessArgument("--qa-overview"));
        Assert.False(QaStartupIsolation.IsHarnessArgument("--qa-overview-scenario"));
        Assert.False(QaStartupIsolation.IsHarnessArgument("--QA-OVERVIEW"));

        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, "--qa-overview"]));
        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, "--qa-overview", "--qa-overview-scenario", "many-500", "--qa-ui-language", "pt-PT"]));
        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, "--qa-overview-scenario=empty", "--qa-overview"]));

        // The modifier alone would run production: refused. Malformed forms are refused next to the harness too.
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-overview-scenario", "mixed"]));
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-overview=1"]));
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-overview", "--qa-overview-scenario="]));
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-overview", "--Qa-Overview-Scenario=mixed"]));

        // An unknown scenario is refused at launch (exit 3), before composition - not by a crash inside it.
        var unknown = QaStartupIsolation.LaunchRefusal([Exe, "--qa-overview", "--qa-overview-scenario", "bogus"]);
        Assert.NotNull(unknown);
        Assert.Contains("many-500", unknown);
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-overview", "--qa-overview-scenario"]));

        // Next to another harness the modifier would be silently ignored: refused (Cortex r1 NIT-4).
        var foreign = QaStartupIsolation.LaunchRefusal([Exe, "--qa-health", "--qa-overview-scenario", "mixed"]);
        Assert.NotNull(foreign);
        Assert.Contains("--qa-overview", foreign);
    }

    [Fact]
    public void IsRequested_IsExactAndOrdinal_AndOffInTheTestHost()
    {
        Assert.False(QaOverviewComposition.IsRequested());
        Assert.True(QaOverviewComposition.IsRequested([Exe, "--qa-overview"]));
        Assert.False(QaOverviewComposition.IsRequested([Exe, "--QA-OVERVIEW"]));
        Assert.False(QaOverviewComposition.IsRequested([Exe, "--qa-overview-scenario", "mixed"]));
    }

    [Fact]
    public void ScenarioPolicy_ResolvesKnownNames_IgnoresReleaseAndUnknown()
    {
        Assert.Equal("many-500", QaOverviewScenarioPolicy.ResolveScenario([Exe, "--qa-overview-scenario", "many-500"], isDebugBuild: true));
        // Ordinal, like the strict parser (Cortex r1 NIT-4): neither the flag nor the value is case-folded.
        Assert.Null(QaOverviewScenarioPolicy.ResolveScenario([Exe, "--qa-overview-scenario", "MANY-500"], isDebugBuild: true));
        Assert.False(QaOverviewScenarioPolicy.IsPresent([Exe, "--QA-OVERVIEW-SCENARIO", "mixed"]));
        Assert.Equal("empty", QaOverviewScenarioPolicy.ResolveScenario([Exe, "--qa-overview-scenario=empty"], isDebugBuild: true));
        Assert.Null(QaOverviewScenarioPolicy.ResolveScenario([Exe, "--qa-overview-scenario", "mixed"], isDebugBuild: false));
        Assert.Null(QaOverviewScenarioPolicy.ResolveScenario([Exe, "--qa-overview-scenario", "made-up"], isDebugBuild: true));
        Assert.Null(QaOverviewScenarioPolicy.ResolveScenario([Exe, "--qa-overview-scenario"], isDebugBuild: true));
    }

    [Fact]
    public void AnUnknownScenario_IsRefused_NeverReplacedByAnother()
    {
        Assert.Equal(QaOverviewScenarioPolicy.DefaultScenario, QaOverviewComposition.RequestedScenario([Exe, "--qa-overview"]));
        Assert.Equal("offline", QaOverviewComposition.RequestedScenario([Exe, "--qa-overview", "--qa-overview-scenario", "offline"]));

        var refusal = Assert.Throws<InvalidOperationException>(
            () => QaOverviewComposition.RequestedScenario([Exe, "--qa-overview", "--qa-overview-scenario", "made-up"]));
        Assert.Contains("many-500", refusal.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => QaOverviewCatalog.Build("made-up"));
    }

    // ---- composition ------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void TheHarnessDelta_RegistersOnlyInMemoryDoubles(string scenario)
    {
        var services = new ServiceCollection();
        // UI.5: the Settings / Data scenarios compose only next to the --qa-backup doubles (refusal tested separately).
        QaOverviewComposition.Apply(services, scenario, backupDoublesRequested: QaOverviewScenarioPolicy.RequiresBackupDouble(scenario));

        Assert.All(services, descriptor =>
        {
            var implementation = descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
            Assert.True(implementation is not null, $"{descriptor.ServiceType.Name} is registered through a factory");
            Assert.True(
                implementation!.Namespace == typeof(QaOverviewComposition).Namespace
                    || implementation == typeof(ServerMonitoringStateStore)
                    // UI.5: the in-memory connection-state store, seeded with synthetic auth / host-key results.
                    || implementation == typeof(ServerConnectionStateStore)
                    || implementation == typeof(WindowPlacementStorageOptions)
                    || implementation == typeof(PresentationClock),
                $"{scenario}: {descriptor.ServiceType.Name} -> {implementation.FullName} is not a QA double");
        });

        // No container-wide TimeProvider: only the overview's clock is fixed (the tray / alerts keep the system clock).
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(TimeProvider));
        var placement = (WindowPlacementStorageOptions)services.Last(d => d.ServiceType == typeof(WindowPlacementStorageOptions)).ImplementationInstance!;
        Assert.True(QaStartupIsolation.IsUnder(Path.GetFullPath(placement.FilePath), QaTestRoots.Root), placement.FilePath);
    }

    [Fact]
    public void OverTheRealCompositionRoot_TheHarnessWinsForEveryDataPlaneService()
    {
        using var composition = new TestSupport.IsolatedAppComposition();
        QaOverviewComposition.Apply(composition.Services, "mixed");
        using var provider = composition.BuildProvider();

        Assert.IsType<QaOverviewServerService>(provider.GetRequiredService<IServerService>());
        Assert.IsType<QaOverviewMetricsStore>(provider.GetRequiredService<IServerMetricsStore>());
        Assert.IsType<QaOverviewMonitoringEngine>(provider.GetRequiredService<IMonitoringEngine>());
        Assert.IsType<ServerConnectionStateStore>(provider.GetRequiredService<IServerConnectionStateStore>());
        Assert.IsType<QaDiscoveryService>(provider.GetRequiredService<IServerDiscoveryService>());
        Assert.Equal(QaOverviewCatalog.Now, provider.GetRequiredService<PresentationClock>().UtcNow);
        // The production root registers ONE MonitoringOptions, the instance the engine is built with.
        Assert.Equal(MonitoringOptions.Default.Thresholds, provider.GetRequiredService<MonitoringOptions>().Thresholds);
    }

    // ---- catalogue ---------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void TheCatalogue_IsSyntheticDeterministic_AndConsistentWithTheEngine(string name)
    {
        var first = QaOverviewCatalog.Build(name);
        var second = QaOverviewCatalog.Build(name);

        Assert.Equal(first.Servers.Select(s => (s.Server.Id, s.Server.Name, s.Server.Host)), second.Servers.Select(s => (s.Server.Id, s.Server.Name, s.Server.Host)));
        Assert.Equal(first.Servers.Count, first.Servers.Select(s => s.Server.Id).Distinct().Count());
        Assert.All(first.Servers, entry =>
        {
            // .local names or documentation addresses only (RFC 5737 192.0.2.0/24; UI.5 adds RFC 3849 2001:db8::/32 for A-13).
            Assert.True(entry.Server.Host.EndsWith(".local", StringComparison.Ordinal)
                || entry.Server.Host.StartsWith("192.0.2.", StringComparison.Ordinal)
                || entry.Server.Host.StartsWith("2001:db8:", StringComparison.Ordinal), entry.Server.Host);
            if (entry.Server.Route?.Jump is { } jump)
            {
                Assert.EndsWith(".local", jump.Host, StringComparison.Ordinal);
                Assert.Null(jump.PrivateKeyPath);
                Assert.Null(jump.CredentialReferenceId);
            }
            Assert.Equal(entry.Server.Id, entry.State.ServerId);
            if (entry.Snapshot is { } snapshot)
            {
                Assert.Equal(entry.Server.Id, snapshot.ServerId);
            }

            // Reachable states are exactly what the engine derives from the snapshot (nothing invented for the screen).
            if (entry.State.Health is ServerHealth.Healthy or ServerHealth.Warning or ServerHealth.Critical)
            {
                Assert.Equal(HealthEvaluator.EvaluateFromMetrics(entry.Snapshot, MonitoringThresholds.Default), entry.State.Health);
            }
        });
    }

    /// <summary>vanishing: once its servers go, the change is announced and every later load is empty (in memory).</summary>
    [Fact]
    public async Task Vanishing_AnnouncesTheChange_AndLaterLoadsAreEmpty()
    {
        var scheduled = new List<(TimeSpan Delay, Action Run)>();
        var service = new QaOverviewServerService(QaOverviewCatalog.Build("vanishing"), (delay, run) => scheduled.Add((delay, run)));
        Assert.NotEmpty(await service.GetAllAsync());
        Assert.NotEmpty(await service.GetAllAsync());
        var (delay, run) = Assert.Single(scheduled); // scheduled once, by the first load
        Assert.Equal(TimeSpan.FromSeconds(30), delay);
        var raised = 0;
        service.ServersChanged += (_, _) => raised++;

        run();

        Assert.Equal(1, raised);
        Assert.Empty(await service.GetAllAsync());
        Assert.Single(scheduled);
    }

    [Fact]
    public async Task TheScenarios_HaveTheIntendedShape()
    {
        var mixed = QaOverviewCatalog.Build("mixed");
        Assert.Equal(["prod-web-01", "prod-db-01", "staging-01", "docker-host", "mac-mini", "backup-nas", "old-build-01"], mixed.Servers.Select(s => s.Server.Name));
        Assert.Single(mixed.Servers, s => s.Server.IsHidden);

        Assert.Equal(100, QaOverviewCatalog.Build("many-100").Servers.Count);
        var many = QaOverviewCatalog.Build("many-500").Servers;
        Assert.Equal(500, many.Count);
        foreach (var health in Enum.GetValues<ServerHealth>())
        {
            Assert.Contains(many, s => s.State.Health == health);
        }

        Assert.Empty(QaOverviewCatalog.Build("empty").Servers);
        Assert.NotEmpty(QaOverviewCatalog.Build("discovery").Discovered);
        Assert.All(QaOverviewCatalog.Build("unavailable").Servers, s => Assert.Null(s.Snapshot));
        Assert.All(QaOverviewCatalog.Build("healthy").Servers, s => Assert.Equal(ServerHealth.Healthy, s.State.Health));

        var vanishing = QaOverviewCatalog.Build("vanishing");
        Assert.Equal(mixed.Servers.Select(s => s.Server.Name), vanishing.Servers.Select(s => s.Server.Name));
        Assert.Equal(TimeSpan.FromSeconds(30), vanishing.VanishAfter);
        Assert.Null(mixed.VanishAfter);

        var loading = new QaOverviewServerService(QaOverviewCatalog.Build("loading")).GetAllAsync();
        await Task.Delay(50);
        Assert.False(loading.IsCompleted);
    }

    [Theory]
    [InlineData("mixed", "prod-db-01", PriorityMetric.Disk, ServerHealth.Warning)]
    [InlineData("attention", "api-02", PriorityMetric.Memory, ServerHealth.Warning)]
    [InlineData("critical", "cache-01", PriorityMetric.Cpu, ServerHealth.Critical)]
    [InlineData("offline", null, null, null)]
    [InlineData("healthy", null, null, null)]
    [InlineData("unavailable", null, null, null)]
    public async Task EveryScenario_DrivesTheRealDashboard_ToItsIntendedPriority(string scenario, string? server, PriorityMetric? metric, ServerHealth? severity)
    {
        var vm = Dashboard(scenario);
        await vm.LoadAsync();

        Assert.Equal(server, vm.PriorityServerName);
        Assert.Equal(metric, vm.PriorityProblem?.Metric);
        Assert.Equal(severity, vm.PriorityProblem?.Severity);
        // No candidate → the neutral card: "Sem problemas" with readings, "Sem leituras" without (Prism r1 e).
        Assert.Equal(server is null, vm.ShowNoProblems || vm.ShowNoReadings);
        Assert.Equal(scenario == "unavailable", vm.ShowNoReadings);
        // Fixed clock: "há 8 s" everywhere something was collected; nothing collected = no freshness line.
        Assert.Equal(scenario == "unavailable" ? null : "OverviewUpdatedSecondsFormat", vm.UpdatedAgoDisplay);
    }

    private static DashboardViewModel Dashboard(string name)
    {
        var services = new ServiceCollection();
        QaOverviewComposition.Apply(services, name);
        var provider = services.BuildServiceProvider();
        return new DashboardViewModel(
            provider.GetRequiredService<IServerService>(),
            null!,
            null!,
            new FakeConnectionStateStore(),
            provider.GetRequiredService<IServerMetricsStore>(),
            provider.GetRequiredService<IServerMonitoringStateStore>(),
            provider.GetRequiredService<IMonitoringEngine>(),
            provider.GetRequiredService<IServerDiscoveryService>(),
            new FakeNavigationService(),
            new FakeLocalizationService(),
            NullLogger<DashboardViewModel>.Instance,
            clock: provider.GetRequiredService<PresentationClock>());
    }
}
