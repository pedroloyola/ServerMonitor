using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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
/// UI.8 §3: the Debug-only Compact Mode harness (<c>--qa-compact</c> + <c>--qa-compact-scenario</c> /
/// <c>--qa-compact-start</c> / <c>--qa-compact-ticker</c>). It starts again (the load-status source the composition root
/// casts to), its parser is strict (closed scenario list, exact values, no clamp, no default for a bad value), its
/// catalogue is synthetic, deterministic and consistent with the engine, its placement never reaches a file, and its
/// ticker publishes through the real state store on an injected clock. Pure: no launch, no real path, no network.
/// </summary>
public sealed class QaCompactHarnessTests
{
    private const string Exe = @"C:\fixture\ServerMonitor.App.exe";

    public static TheoryData<string> Scenarios => new(QaCompactCatalog.Scenarios);

    // ---- parser / launcher -------------------------------------------------------------------------------------

    [Fact]
    public void HarnessIsNotRequestedByDefault()
    {
        Assert.False(QaCompactComposition.IsRequested());
        Assert.False(QaCompactComposition.TickerRequested());
    }

    [Fact]
    public void TheFlagAndTheModifiers_AreInTheStrictParser()
    {
        Assert.Contains(QaCompactComposition.LaunchFlag, QaStartupIsolation.HarnessFlags);
        Assert.Contains(QaCompactComposition.ScenarioFlag, QaStartupIsolation.ModifierFlags);
        Assert.Contains(QaCompactComposition.StartFlag, QaStartupIsolation.ModifierFlags);
        Assert.Contains(QaCompactComposition.TickerFlag, QaStartupIsolation.ModifierFlags);
    }

    public static TheoryData<string[], string, WindowMode, string?> AcceptedLaunches => new()
    {
        { ["--qa-compact"], "figma", WindowMode.Compact, null },
        { ["--qa-compact", "--qa-compact-scenario", "n200"], "n200", WindowMode.Compact, null },
        { ["--qa-compact", "--qa-compact-scenario=all-hidden", "--qa-compact-start=standard"], "all-hidden", WindowMode.Standard, null },
        { ["--qa-compact-start", "compact", "--qa-compact", "--qa-compact-ticker", "identical"], "figma", WindowMode.Compact, "Identical" },
        { ["--qa-compact", "--qa-compact-ticker=varying", "--qa-compact-scenario", "mixed", "--qa-ui-language", "en-US"], "mixed", WindowMode.Compact, "Varying" }
    };

    [Theory]
    [MemberData(nameof(AcceptedLaunches))]
    public void AWellFormedLaunch_IsAllowed_AndParsedExactly(string[] arguments, string scenario, WindowMode start, string? ticker)
    {
        string[] args = [Exe, .. arguments];

        Assert.Null(QaStartupIsolation.LaunchRefusal(args));
        Assert.True(QaStartupIsolation.IsHarnessLaunch(args));
        var expectedTicker = ticker is null ? (QaCompactTickerMode?)null : Enum.Parse<QaCompactTickerMode>(ticker);
        Assert.Equal(new QaCompactLaunch(scenario, start, expectedTicker), QaCompactComposition.Parse(args));
    }

    public static TheoryData<string[]> RefusedLaunches => new()
    {
        { ["--qa-compact:12"] },                                                 // the retired count form (was a silent 0-40 clamp)
        { ["--qa-compact:0"] },
        { ["--qa-compact", "--qa-compact-scenario", "nope"] },                   // unknown scenario: never another one
        { ["--qa-compact", "--qa-compact-scenario", "N20"] },                    // ordinal
        { ["--qa-compact", "--qa-compact-scenario", "n50"] },                    // no count outside the closed list
        { ["--qa-compact", "--qa-compact-scenario"] },                           // missing value
        { ["--qa-compact", "--qa-compact-scenario", "--qa-compact-start=standard"] }, // a switch is not a value
        { ["--qa-compact", "--qa-compact-scenario=figma", "--qa-compact-scenario=one"] }, // at most once
        { ["--qa-compact", "--qa-compact-start=maximized"] },
        { ["--qa-compact", "--qa-compact-ticker", "fast"] },
        { ["--qa-health", "--qa-compact-scenario", "figma"] },                   // only next to --qa-compact
        { ["--qa-compact-scenario", "figma"] },                                  // a modifier alone
        { ["--qa-compact", "--qa-compact-scenarios=figma"] }                     // not a recognised switch
    };

    [Theory]
    [MemberData(nameof(RefusedLaunches))]
    public void AMalformedCompactLaunch_IsRefused_BeforeAnythingIsComposed(string[] arguments)
    {
        string[] args = [Exe, .. arguments];

        Assert.NotNull(QaStartupIsolation.LaunchRefusal(args));
        Assert.Null(QaCompactComposition.Parse(args));
    }

    /// <summary>
    /// UI.8 RC-4 QA: an external activation on the compact harness (e.g. with --background, Compact persisted in memory)
    /// - dashboard or server:&lt;n&gt; over the scenario's VISIBLE servers; never --qa-start, never out of range.
    /// </summary>
    [Fact]
    public void AnActivation_OnTheCompactHarness_IsStrict_AndResolvesTheScenariosVisibleServer()
    {
        string[] server = [Exe, "--qa-compact", "--background", "--qa-activation=server:6"];
        Assert.Null(QaStartupIsolation.LaunchRefusal(server));
        Assert.Equal(ServerMonitor.ActivationContract.ActivationIntent.Server(QaCompactCatalog.Build("figma").Servers[5].Id), QaShellStartup.Activation(server));
        Assert.Equal(ServerMonitor.ActivationContract.ActivationIntent.Dashboard,
            QaShellStartup.Activation([Exe, "--qa-compact", "--qa-compact-scenario", "one", "--qa-activation=dashboard"]));

        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-compact", "--qa-activation=server:7"]));      // figma has 6
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-compact", "--qa-compact-scenario=all-hidden", "--qa-activation=server:1"]));
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-compact", "--qa-start=overview"]));
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-compact", "--qa-activation=server:x"]));
        Assert.NotNull(QaStartupIsolation.LaunchRefusal([Exe, "--qa-compact", "--qa-compact-scenario=nope", "--qa-activation=dashboard"]));
    }

    [Fact]
    public void AnUnknownScenario_IsNeverBuilt()
    {
        Assert.Throws<ArgumentException>(() => QaCompactCatalog.Build("nope"));
        Assert.Throws<ArgumentException>(() => QaCompactCatalog.Build("n40"));
    }

    // ---- catalogue ---------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void TheCatalogue_IsSyntheticDeterministic_AndConsistentWithTheEngine(string name)
    {
        var first = QaCompactCatalog.Build(name);
        var second = QaCompactCatalog.Build(name);

        Assert.Equal(first.Entries, second.Entries);
        Assert.Equal(first.Servers.Count, first.Servers.Select(s => s.Id).Distinct().Count());
        Assert.All(first.Entries, entry =>
        {
            Assert.StartsWith("c0c0c0c0-0008-", entry.Server.Id.ToString(), StringComparison.Ordinal);
            Assert.EndsWith(".local", entry.Server.Host, StringComparison.Ordinal);
            Assert.Equal(entry.Server.Id, entry.State.ServerId);
            if (entry.Snapshot is { } snapshot)
            {
                Assert.Equal(entry.Server.Id, snapshot.ServerId);
                Assert.True(snapshot.CollectedAt <= QaCompactCatalog.Now);
            }

            if (entry.State.Health is ServerHealth.Healthy or ServerHealth.Warning or ServerHealth.Critical)
            {
                Assert.Equal(HealthEvaluator.EvaluateFromMetrics(entry.Snapshot, MonitoringThresholds.Default), entry.State.Health);
            }
        });
    }

    [Fact]
    public void TheScenarios_HaveTheIntendedShape()
    {
        var figma = QaCompactCatalog.Build("figma");
        Assert.Equal(["prod-web-01", "prod-db-01", "staging-01", "docker-host", "mac-mini", "backup-nas"], figma.Servers.Select(s => s.Name));
        Assert.Equal(
            [ServerHealth.Healthy, ServerHealth.Warning, ServerHealth.Healthy, ServerHealth.Healthy, ServerHealth.Healthy, ServerHealth.Offline],
            figma.Entries.Select(e => e.State.Health));
        Assert.Equal(QaCompactCatalog.DefaultScenario, figma.Name);

        Assert.Equal(20, QaCompactCatalog.Build("n20").Servers.Count);
        Assert.Equal(100, QaCompactCatalog.Build("n100").Servers.Count);
        var n200 = QaCompactCatalog.Build("n200");
        Assert.Equal(200, n200.Servers.Count); // no clamp
        foreach (var health in Enum.GetValues<ServerHealth>())
        {
            Assert.Contains(n200.Entries, e => e.State.Health == health);
        }

        Assert.Empty(QaCompactCatalog.Build("empty").Servers);
        Assert.Equal(ServerLoadStatus.Loaded, QaCompactCatalog.Build("empty").LoadStatus);
        Assert.Empty(QaCompactCatalog.Build("config-unavailable").Servers);
        Assert.Equal(ServerLoadStatus.Unavailable, QaCompactCatalog.Build("config-unavailable").LoadStatus);
        Assert.All(QaCompactCatalog.Build("all-hidden").Servers, s => Assert.True(s.IsHidden));
        Assert.All(QaCompactCatalog.Build("offline").Entries, e =>
        {
            Assert.Equal(ServerHealth.Offline, e.State.Health);
            Assert.NotNull(e.Snapshot); // a retained reading the compact row must NOT show
        });
        Assert.All(QaCompactCatalog.Build("stale").Entries, e => Assert.True(e.State.IsStale));
        Assert.Contains(QaCompactCatalog.Build("long-names").Servers, s => s.Name.Length > 50);
        Assert.Contains(QaCompactCatalog.Build("mixed").Entries, e => e.Snapshot is { MemoryUsagePercent: null }); // unknown stays null
    }

    [Fact]
    public async Task TheLoadingScenario_NeverCompletes_AndTheServiceIsTheLoadStatusSource()
    {
        var loading = new QaCompactServerService(QaCompactCatalog.Build("loading"));
        Assert.False(loading.GetAllAsync().IsCompleted); // a TaskCompletionSource nobody completes: no timing involved

        // The composition root casts IServerService to IServerLoadStatusSource; without it the window never resolved.
        IServerService unavailable = new QaCompactServerService(QaCompactCatalog.Build("config-unavailable"));
        var source = Assert.IsAssignableFrom<IServerLoadStatusSource>(unavailable);
        Assert.Equal(ServerLoadStatus.Unavailable, await source.GetLoadStatusAsync());
    }

    // ---- composition / isolation -------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void TheHarnessDelta_RegistersOnlyInMemoryDoubles(string scenario)
    {
        var services = new ServiceCollection();
        QaCompactComposition.Apply(services, new QaCompactLaunch(scenario, WindowMode.Compact, QaCompactTickerMode.Identical));

        Assert.All(services, descriptor =>
        {
            var implementation = descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
            Assert.True(implementation is not null, $"{descriptor.ServiceType.Name} is registered through a factory");
            Assert.True(
                implementation!.Namespace == typeof(QaCompactComposition).Namespace
                    || implementation == typeof(ServerMonitoringStateStore)
                    || implementation == typeof(PresentationClock),
                $"{scenario}: {descriptor.ServiceType.Name} -> {implementation.FullName} is not a QA double");
        });
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(TimeProvider));
    }

    /// <summary>
    /// Over the REAL composition root (TEST-REALDATA-AUDIT: every per-user root re-pointed at a temp directory first),
    /// the harness wins for every data-plane service, the load-status cast the root makes resolves to the harness, the
    /// clock is fixed, and the window placement is the in-memory store - so a Standard ⇄ Compact round trip in the QA
    /// window keeps its geometry for the session and no placement file is ever opened. The real-path re-root of every
    /// other store is QaStartupIsolation's (QaStartupIsolationTests, registered before every harness).
    /// </summary>
    [Fact]
    public void OverTheRealCompositionRoot_TheHarnessWins_AndThePlacementNeverReachesAFile()
    {
        using var composition = new TestSupport.IsolatedAppComposition();
        QaCompactComposition.Apply(composition.Services, new QaCompactLaunch("figma", WindowMode.Standard, null));
        using var provider = composition.BuildProvider();

        Assert.IsType<QaCompactServerService>(provider.GetRequiredService<IServerService>());
        Assert.Same(provider.GetRequiredService<IServerService>(), provider.GetRequiredService<IServerLoadStatusSource>());
        Assert.IsType<QaCompactMetricsStore>(provider.GetRequiredService<IServerMetricsStore>());
        Assert.IsType<QaMonitoringEngine>(provider.GetRequiredService<IMonitoringEngine>());
        Assert.Equal(QaCompactCatalog.Now, provider.GetRequiredService<PresentationClock>().UtcNow);
        var placement = Assert.IsType<QaCompactPlacementStore>(provider.GetRequiredService<IWindowPlacementStore>());
        Assert.Equal(WindowMode.Standard, placement.Load().Mode);

        var file = provider.GetRequiredService<WindowPlacementStorageOptions>().FilePath;
        var coordinator = new WindowModeCoordinator(new Fakes.RecordingPlacementAdapter(), placement, NullLogger<WindowModeCoordinator>.Instance);
        coordinator.Initialize();
        coordinator.SwitchTo(WindowMode.Compact);
        coordinator.PersistCurrentBounds();

        Assert.Equal(WindowMode.Compact, placement.Load().Mode);
        Assert.False(File.Exists(file), $"the QA compact window wrote a placement file: {file}");
    }

    [Fact]
    public void ThePlacementStore_KeepsTheSessionInMemory_AndHasNoFile()
    {
        var store = new QaCompactPlacementStore(WindowMode.Compact);
        Assert.Equal(WindowPlacementSettings.Default with { Mode = WindowMode.Compact }, store.Load());

        var moved = new WindowPlacementSettings { Mode = WindowMode.Standard, CompactBounds = new WindowBounds(10, 20, 432, 704) };
        store.Save(moved);

        Assert.Equal(moved, store.Load());
        Assert.DoesNotContain(typeof(QaCompactPlacementStore).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(WindowPlacementStorageOptions));
    }

    // ---- ticker (fake time) ------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTicker_PublishesOnlyOnItsInjectedClock_OneStatePerVisibleServer()
    {
        var (ticker, states, _) = Ticker("figma", QaCompactTickerMode.Identical, out var time);
        var published = new List<Guid>();
        states.StateChanged += (_, id) => published.Add(id);

        await ticker.StartAsync(CancellationToken.None);
        time.Advance(QaCompactTicker.Interval - TimeSpan.FromMilliseconds(1));
        Assert.Empty(published);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(QaCompactCatalog.Build("figma").Servers.Select(s => s.Id), published);
        Assert.Equal(1, ticker.Ticks);

        await ticker.StopAsync(CancellationToken.None);
        time.Advance(QaCompactTicker.Interval * 3);
        Assert.Equal(1, ticker.Ticks);
        Assert.Equal(6, published.Count);
    }

    [Fact]
    public async Task TheTicker_SkipsHiddenServers()
    {
        var (ticker, states, _) = Ticker("all-hidden", QaCompactTickerMode.Varying, out var time);
        var published = 0;
        states.StateChanged += (_, _) => published++;

        await ticker.StartAsync(CancellationToken.None);
        time.Advance(QaCompactTicker.Interval);
        time.Advance(QaCompactTicker.Interval);

        Assert.Equal(2, ticker.Ticks);
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task TheIdenticalTicker_RepublishesTheSameStates_AndTheVaryingOne_MovesReadingsByTheEngineRule()
    {
        var catalog = QaCompactCatalog.Build("figma");
        var (identical, identicalStates, identicalMetrics) = Ticker("figma", QaCompactTickerMode.Identical, out var identicalTime);
        await identical.StartAsync(CancellationToken.None);
        identicalTime.Advance(QaCompactTicker.Interval);
        identicalTime.Advance(QaCompactTicker.Interval);
        Assert.Equal(2, identical.Ticks);
        Assert.All(catalog.Entries, entry =>
        {
            Assert.Equal(entry.State, identicalStates.Get(entry.Server.Id));
            Assert.Equal(entry.Snapshot, identicalMetrics.GetLastSnapshot(entry.Server.Id));
        });

        var (varying, varyingStates, varyingMetrics) = Ticker("figma", QaCompactTickerMode.Varying, out var varyingTime);
        await varying.StartAsync(CancellationToken.None);
        varyingTime.Advance(QaCompactTicker.Interval);
        var web = catalog.Entries[0];
        var moved = varyingMetrics.GetLastSnapshot(web.Server.Id)!;
        Assert.Equal(QaCompactTicker.Vary(24, 1, 0), moved.CpuUsagePercent);
        Assert.NotEqual(web.Snapshot!.CpuUsagePercent, moved.CpuUsagePercent);
        Assert.Equal(HealthEvaluator.EvaluateFromMetrics(moved), varyingStates.Get(web.Server.Id).Health);
        // Without connection: the retained reading never moves and the server stays Offline.
        var nas = catalog.Entries[5];
        Assert.Equal(nas.Snapshot, varyingMetrics.GetLastSnapshot(nas.Server.Id));
        Assert.Equal(ServerHealth.Offline, varyingStates.Get(nas.Server.Id).Health);
        Assert.Null(QaCompactTicker.Vary(null, 7, 1)); // unknown stays unknown, never 0
    }

    /// <summary>The ticker drives the REAL dashboard through the store's StateChanged (the production path).</summary>
    [Fact]
    public async Task TheVaryingTicker_ReachesTheRealDashboardCards_ThroughStateChanged()
    {
        var services = new ServiceCollection();
        QaCompactComposition.Apply(services, new QaCompactLaunch("figma", WindowMode.Compact, null));
        using var provider = services.BuildServiceProvider();
        var time = new FakeTimeProvider(QaCompactCatalog.Now);
        var ticker = new QaCompactTicker(
            provider.GetRequiredService<QaCompactCatalog>(),
            (QaCompactMetricsStore)provider.GetRequiredService<IServerMetricsStore>(),
            provider.GetRequiredService<IServerMonitoringStateStore>(),
            QaCompactTickerMode.Varying,
            time);
        var dashboard = Dashboard(provider);
        await dashboard.LoadAsync();
        var web = dashboard.VisibleServers[0];
        Assert.Equal(24, web.CpuUsageValue);

        await ticker.StartAsync(CancellationToken.None);
        time.Advance(QaCompactTicker.Interval);

        Assert.Equal(QaCompactTicker.Vary(24, 1, 0), web.CpuUsageValue);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task EveryScenario_DrivesTheRealDashboard_ToItsIntendedBodyState(string scenario)
    {
        var services = new ServiceCollection();
        QaCompactComposition.Apply(services, new QaCompactLaunch(scenario, WindowMode.Compact, null));
        using var provider = services.BuildServiceProvider();
        var dashboard = Dashboard(provider);

        var load = dashboard.LoadAsync();
        if (scenario == "loading")
        {
            Assert.False(load.IsCompleted);
            Assert.True(dashboard.IsLoading);
            return;
        }

        await load;
        var visible = QaCompactCatalog.Build(scenario).Servers.Where(s => !s.IsHidden).Select(s => s.Id);
        Assert.Equal(visible, dashboard.VisibleServers.Select(card => card.Server.Id));
        Assert.Equal(scenario is "empty", dashboard.ShowFirstServerState);
        Assert.Equal(scenario is "all-hidden", dashboard.ShowAllHiddenState);
        Assert.Equal(scenario is "config-unavailable", dashboard.ShowConfigurationUnavailable);
    }

    private static (QaCompactTicker Ticker, ServerMonitoringStateStore States, QaCompactMetricsStore Metrics) Ticker(
        string scenario, QaCompactTickerMode mode, out FakeTimeProvider time)
    {
        var catalog = QaCompactCatalog.Build(scenario);
        var states = new ServerMonitoringStateStore();
        foreach (var entry in catalog.Entries)
        {
            states.Set(entry.State);
        }

        var metrics = new QaCompactMetricsStore(catalog);
        time = new FakeTimeProvider(QaCompactCatalog.Now);
        return (new QaCompactTicker(catalog, metrics, states, mode, time), states, metrics);
    }

    private static DashboardViewModel Dashboard(IServiceProvider provider) => new(
        provider.GetRequiredService<IServerService>(),
        null!,
        null!,
        new InertEditorSession(),
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
