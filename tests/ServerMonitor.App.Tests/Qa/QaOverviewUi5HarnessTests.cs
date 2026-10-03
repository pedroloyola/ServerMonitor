using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI.5 §6 (Cortex 3 / 4): the <c>--qa-overview</c> harness extended for Server Detail and Settings/Data. Mutating
/// IN-MEMORY doubles (hide / restore / remove / refresh succeed, or fail on request) close the UI.4 NOT_RUN; the
/// Settings/Data scenarios are fail-closed behind <c>--qa-backup</c>, so the real backup picker is unreachable. Pure: no
/// launch, no real path, no credential, no network.
/// </summary>
public sealed class QaOverviewUi5HarnessTests
{
    private const string Exe = @"C:\fixture\ServerMonitor.App.exe";

    // ---- fail-closed backup isolation for EVERY overview scenario (Cortex 4, Boss B2 answer 5) ---------------------

    public static TheoryData<string?> AllScenarios()
    {
        var data = new TheoryData<string?> { (string?)null }; // no modifier = the default scenario
        foreach (var scenario in QaOverviewScenarioPolicy.Scenarios)
        {
            data.Add(scenario);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllScenarios))]
    public void AnyOverviewLaunch_WithoutTheBackupDoubles_IsRefused(string? scenario)
    {
        string[] Launch(params string[] extra) =>
            [Exe, "--qa-overview", .. scenario is null ? Array.Empty<string>() : ["--qa-overview-scenario", scenario], .. extra];

        var refusal = QaStartupIsolation.LaunchRefusal(Launch());

        Assert.NotNull(refusal);
        Assert.Contains(QaBackupPolicy.LaunchFlag, refusal, StringComparison.Ordinal);
        Assert.Null(QaStartupIsolation.LaunchRefusal(Launch("--qa-backup", "ok")));
        Assert.Null(QaStartupIsolation.LaunchRefusal(Launch("--qa-backup=stuck")));
        // An unknown backup scenario is not "the doubles": still refused.
        Assert.NotNull(QaStartupIsolation.LaunchRefusal(Launch("--qa-backup", "bogus")));
    }

    [Theory]
    [MemberData(nameof(AllScenarios))]
    public void AnyOverviewScenario_WithoutTheBackupDoubles_IsNeverComposed(string? scenario)
    {
        var services = new ServiceCollection();

        var refused = Assert.Throws<InvalidOperationException>(() =>
            QaOverviewComposition.Apply(services, scenario ?? QaOverviewScenarioPolicy.DefaultScenario, backupDoublesRequested: false));

        Assert.Contains(QaBackupPolicy.LaunchFlag, refused.Message, StringComparison.Ordinal);
        Assert.Empty(services); // nothing composed before the refusal
    }

    /// <summary>The rule belongs to --qa-overview: the other harnesses keep their own (UI.3/M14.6) backup wiring.</summary>
    [Fact]
    public void OtherHarnesses_AreUnaffected() =>
        Assert.Null(QaStartupIsolation.LaunchRefusal([Exe, "--qa-health"]));

    /// <summary>With the doubles, the composed picker is the in-memory one: the native picker cannot open.</summary>
    [Fact]
    public void ADataScenario_WithTheDoubles_ResolvesTheInMemoryPicker()
    {
        using var composition = new TestSupport.IsolatedAppComposition();
        QaOverviewComposition.Apply(composition.Services, "data", backupDoublesRequested: true);
        QaBackupScenarioComposition.Apply(composition.Services, "ok");
        using var provider = composition.BuildProvider();

        Assert.IsType<QaBackupFilePicker>(provider.GetRequiredService<IBackupFilePicker>());
        Assert.IsNotType<BackupFilePicker>(provider.GetRequiredService<IBackupFilePicker>());
    }

    // ---- mutating doubles (Cortex 3) ------------------------------------------------------------------------------

    [Theory]
    [InlineData("detail")]
    [InlineData("data")]
    public async Task InAMutableScenario_HideRestoreRemove_ReallyChangeTheInMemoryList(string scenario)
    {
        var service = new QaOverviewServerService(QaOverviewCatalog.Build(scenario));
        var changes = 0;
        service.ServersChanged += (_, _) => changes++;
        var web = QaOverviewCatalog.StableId("prod-web-01");
        var hidden = QaOverviewCatalog.StableId("old-build-01");

        Assert.True(await service.HideAsync(web));
        Assert.True((await service.GetAllAsync()).Single(server => server.Id == web).IsHidden);
        Assert.False(await service.HideAsync(web)); // already hidden: no change, no event
        Assert.True(await service.RestoreAsync(hidden));
        Assert.False((await service.GetAllAsync()).Single(server => server.Id == hidden).IsHidden);
        Assert.True(await service.RemoveAsync(web));
        Assert.DoesNotContain(await service.GetAllAsync(), server => server.Id == web);

        Assert.Equal(3, changes);
    }

    [Theory]
    [InlineData("detail-failing")]
    [InlineData("data-failing")]
    [InlineData("mixed")] // UI.4 scenarios stay inert
    public async Task InAFailingOrUi4Scenario_EveryMutationFails_AndNothingChanges(string scenario)
    {
        var built = QaOverviewCatalog.Build(scenario);
        var service = new QaOverviewServerService(built);
        var changes = 0;
        service.ServersChanged += (_, _) => changes++;
        var any = built.Servers[0].Server.Id;

        Assert.False(await service.HideAsync(any));
        Assert.False(await service.RestoreAsync(QaOverviewCatalog.StableId("old-build-01")));
        Assert.False(await service.RemoveAsync(any));

        Assert.Equal(0, changes);
        Assert.Equal(built.Servers.Select(entry => entry.Server), await service.GetAllAsync());
    }

    [Fact]
    public async Task ASuccessfulRefresh_PublishesAFreshReading_AtTheFixedClock()
    {
        var scenario = QaOverviewCatalog.Build("detail");
        var (engine, metrics, states) = Engine(scenario);
        var nas = QaOverviewCatalog.StableId("backup-nas"); // offline, retained snapshot, stale
        var published = new List<Guid>();
        states.StateChanged += (_, id) => published.Add(id);

        var result = await engine.RefreshNowAsync(nas);

        Assert.True(result.IsSuccess);
        Assert.Equal(QaOverviewCatalog.Now, metrics.GetLastSnapshot(nas)!.CollectedAt);
        var state = states.Get(nas);
        Assert.Equal((false, QaOverviewCatalog.Now, (MetricsCollectionErrorCode?)null), (state.IsStale, state.LastSuccessAt!.Value, state.LastError));
        Assert.Equal(Core.Monitoring.HealthEvaluator.EvaluateFromMetrics(metrics.GetLastSnapshot(nas)), state.Health);
        Assert.Equal([nas], published);
    }

    [Theory]
    [InlineData("detail-failing", "prod-web-01")]
    [InlineData("detail", "lab-pi")] // no snapshot to refresh: fails, nothing invented
    [InlineData("mixed", "prod-web-01")]
    public async Task AFailingRefresh_ChangesNothing(string scenarioName, string server)
    {
        var scenario = QaOverviewCatalog.Build(scenarioName);
        var (engine, metrics, states) = Engine(scenario);
        var id = QaOverviewCatalog.StableId(server);
        var before = (states.Get(id), metrics.GetLastSnapshot(id));

        var result = await engine.RefreshNowAsync(id);

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.Unexpected, result.ErrorCode);
        Assert.Equal(before, (states.Get(id), metrics.GetLastSnapshot(id)));
    }

    // ---- states the Detail needs ----------------------------------------------------------------------------------

    [Fact]
    public void TheDetailScenario_SeedsAuthHostKeyAndRoutedServers_Synthetically()
    {
        var services = new ServiceCollection();
        QaOverviewComposition.Apply(services, "detail", backupDoublesRequested: true);
        var connections = (IServerConnectionStateStore)services.Last(d => d.ServiceType == typeof(IServerConnectionStateStore)).ImplementationInstance!;

        Assert.Equal(ServerConnectionState.AuthenticationFailed, connections.Get(QaOverviewCatalog.StableId("auth-01"))!.State);
        Assert.Equal(ServerConnectionState.HostKeyUnknown, connections.Get(QaOverviewCatalog.StableId("hostkey-new-01"))!.State);
        Assert.Equal(ServerConnectionState.HostKeyMismatch, connections.Get(QaOverviewCatalog.StableId("hostkey-changed-01"))!.State);
        Assert.All(new[] { "auth-01", "hostkey-new-01", "hostkey-changed-01" }, name =>
        {
            var result = connections.Get(QaOverviewCatalog.StableId(name))!;
            Assert.Null(result.PresentedHostKey); // no key material, no fingerprint
            Assert.Null(result.TrustedHostKey);
        });

        var routed = QaOverviewCatalog.Build("detail").Servers.Single(entry => entry.Server.Route is not null).Server;
        Assert.Equal("internal-01", routed.Name);
        Assert.Equal("bastion.local", routed.Route!.Jump!.Host);
    }

    [Fact]
    public void TheDetailScenario_CoversEveryDetailState()
    {
        var servers = QaOverviewCatalog.Build("detail").Servers;

        Assert.Contains(servers, e => e.State.Health == ServerHealth.Healthy && e.Snapshot?.Uptime is not null);
        Assert.Contains(servers, e => e.State.Health == ServerHealth.Warning);
        Assert.Contains(servers, e => e.State.Health == ServerHealth.Critical);
        Assert.Contains(servers, e => e.State.Health == ServerHealth.Offline && e.Snapshot is not null && e.State.IsStale);
        Assert.Contains(servers, e => e.State.Health == ServerHealth.Offline && e.Snapshot is null);
        Assert.Contains(servers, e => e.State.Health == ServerHealth.Unknown && e.Snapshot is null && e.State.LastError is null
                                      && e.Server.OperatingSystem == ServerOperatingSystem.Linux); // first reading
        Assert.Contains(servers, e => e.Snapshot is null && e.State.LastError == MetricsCollectionErrorCode.NoMetricsAvailable);
        Assert.Contains(servers, e => e.Snapshot is { CpuUsagePercent: null, MemoryUsagePercent: not null }); // metric unknown
        Assert.Contains(servers, e => e.Server.OperatingSystem == ServerOperatingSystem.Unknown);          // unsupported
        Assert.Contains(servers, e => e.State.IsRefreshing);
        Assert.Contains(servers, e => e.Server.Host.Contains(':', StringComparison.Ordinal));               // IPv6, A-13
        Assert.Contains(servers, e => e.Server.IsHidden);
    }

    /// <summary>H-UI5-3 harness: deterministic synthetic CPU history (in memory) so the pulse can be seen; &gt; 30, &lt; 30 and none.</summary>
    [Fact]
    public async Task TheDetailScenario_ServesDeterministicSyntheticCpuHistory()
    {
        var scenario = QaOverviewCatalog.Build("detail");
        var service = new QaOverviewHistoryQueryService(scenario);

        var web = await service.GetHistoryAsync(QaOverviewCatalog.StableId("prod-web-01"), Core.History.HistoryTimeRange.LastHour);
        var db = await service.GetHistoryAsync(QaOverviewCatalog.StableId("prod-db-01"), Core.History.HistoryTimeRange.LastHour);
        var other = await service.GetHistoryAsync(QaOverviewCatalog.StableId("cache-01"), Core.History.HistoryTimeRange.LastHour);

        Assert.Equal(40, web.Cpu.Points.Count);
        Assert.Single(web.Cpu.Points, point => point.Value is null); // one unmeasured gap, never drawn
        Assert.All(web.Cpu.Points.Where(p => p.Value is not null), p => Assert.InRange(p.Value!.Value, 0, 100));
        Assert.Equal(12, db.Cpu.Points.Count);
        Assert.Empty(other.Cpu.Points);
        Assert.Equal(QaOverviewCatalog.CpuHistory("detail", QaOverviewCatalog.StableId("prod-web-01")),
            QaOverviewCatalog.CpuHistory("detail", QaOverviewCatalog.StableId("prod-web-01")));
        Assert.Empty(QaOverviewCatalog.CpuHistory("mixed", QaOverviewCatalog.StableId("prod-web-01")));
    }

    [Fact]
    public void TheDataScenario_HasHiddenServersToRestore()
    {
        var hidden = QaOverviewCatalog.Build("data").Servers.Where(entry => entry.Server.IsHidden).ToList();

        Assert.Equal(3, hidden.Count);
    }

    private static (QaOverviewMonitoringEngine Engine, QaOverviewMetricsStore Metrics, ServerMonitoringStateStore States) Engine(QaOverviewScenario scenario)
    {
        var metrics = new QaOverviewMetricsStore(scenario);
        var states = new ServerMonitoringStateStore();
        foreach (var entry in scenario.Servers)
        {
            states.Set(entry.State);
        }

        return (new QaOverviewMonitoringEngine(scenario, metrics, states), metrics, states);
    }
}
