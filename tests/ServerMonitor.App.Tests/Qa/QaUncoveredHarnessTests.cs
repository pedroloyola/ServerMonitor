using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.App.Windowing;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI.1 coverage for the three Debug-only harnesses that had none (docs/ui/ui0-figma-audit.md §5 #9):
/// <c>--qa-discovery</c>, <c>--qa-history</c> and <c>--qa-store-screenshot</c>. Each is off by default, its
/// composition registers only QA doubles (no real persistence, credential, SSH or network implementation),
/// and its catalogue is STRUCTURALLY deterministic: the same scenario set, names, states, metric values and
/// flags on every Apply. Timestamps are checked relative to a UtcNow window and IDs are asserted unique and
/// keyed by scenario name, never by GUID value. The placement-isolation guard for every harness is
/// <see cref="QaWindowPlacementIsolationTests"/>; the Release exclusion guard is QaReleaseExclusionGuardTests.
/// </summary>
public sealed class QaUncoveredHarnessTests
{
    public static TheoryData<string> Harnesses => ["discovery", "history", "screenshot"];

    // ---- flags --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("discovery", "--qa-discovery")]
    [InlineData("history", "--qa-history")]
    [InlineData("screenshot", "--qa-store-screenshot")]
    public void FlagIsTheDocumentedQaSwitch_AndBypassesSingleInstancingInDebugOnly(string harness, string flag)
    {
        Assert.Equal(flag, LaunchFlag(harness));
        Assert.Null(SingleInstancePolicy.ResolveInstanceKey(["ServerMonitor.exe", flag], isDebugBuild: true));
        Assert.NotNull(SingleInstancePolicy.ResolveInstanceKey(["ServerMonitor.exe", flag], isDebugBuild: false));
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void HarnessIsNotRequestedByDefault(string harness)
    {
        // The test host is launched without any --qa-* flag, so the real composition stays active.
        Assert.False(IsRequested(harness));
    }

    // ---- composition ------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void CompositionRegistersOnlyQaDoublesOrInMemoryState(string harness)
    {
        var services = new ServiceCollection();
        Apply(harness, services);

        Assert.NotEmpty(services);
        Assert.All(services, descriptor =>
        {
            var implementation = descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
            Assert.True(implementation is not null, $"{descriptor.ServiceType.Name} is registered through a factory; register a QA instance/type instead");
            Assert.True(
                implementation!.Namespace == typeof(QaMonitoringEngine).Namespace
                    || implementation == typeof(ServerMonitoringStateStore)
                    || implementation == typeof(WindowPlacementStorageOptions),
                $"{harness}: {descriptor.ServiceType.Name} -> {implementation.FullName} is not a QA double");
        });
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void CompositionReplacesTheDataPlaneWithQaDoubles(string harness)
    {
        using var provider = Provider(harness);

        AssertQa(provider.GetRequiredService<IServerService>());
        AssertQa(provider.GetRequiredService<IServerMetricsStore>());
        Assert.IsType<QaMonitoringEngine>(provider.GetRequiredService<IMonitoringEngine>());
        Assert.IsType<QaDiscoveryService>(provider.GetRequiredService<IServerDiscoveryService>());
        Assert.IsType<ServerMonitoringStateStore>(provider.GetRequiredService<IServerMonitoringStateStore>());
        if (harness == "history")
        {
            Assert.IsType<QaServerHistoryQueryService>(provider.GetRequiredService<IServerHistoryQueryService>());
        }
    }

    // ---- discovery catalogue ------------------------------------------------------------------------

    [Fact]
    public void Discovery_SeedIsStructurallyDeterministic()
    {
        var first = QaDiscoveryCatalog.Seed();
        var second = QaDiscoveryCatalog.Seed();
        var after = DateTimeOffset.UtcNow;

        foreach (var seed in new[] { first, second })
        {
            Assert.Equal(["Mac Studio", "Raspberry Pi"], seed.Select(s => s.DisplayName));
            Assert.Equal(["mac-studio.local", "raspberrypi.local"], seed.Select(s => s.HostName));
            Assert.Equal(["192.168.1.42", "192.168.1.77"], seed.Select(s => s.Addresses.Single().ToString()));
            Assert.All(seed, s => Assert.Equal(22, s.Port));
            Assert.All(seed, s => Assert.Equal(DiscoverySource.Mdns, s.Source));
            Assert.Equal(seed.Count, seed.Select(s => s.DiscoveryId).Distinct().Count());
            Assert.All(seed, s => AssertInWindow(s.FirstSeenAt, after));
            Assert.All(seed, s => Assert.Equal(s.FirstSeenAt, s.LastSeenAt));
            Assert.Equal(TimeSpan.FromSeconds(1), seed[1].FirstSeenAt - seed[0].FirstSeenAt);
        }

        // Keyed by name, every Seed() describes the same suggestion set.
        Assert.Equal(
            first.ToDictionary(s => s.DisplayName, s => s.DiscoveryId),
            second.ToDictionary(s => s.DisplayName, s => s.DiscoveryId));
    }

    [Fact]
    public void Discovery_CompositionServesTheSeedThroughTheQaService()
    {
        using var provider = Provider("discovery");

        Assert.Empty(provider.GetRequiredService<IServerService>().GetAllAsync().GetAwaiter().GetResult());
        Assert.Equal(
            QaDiscoveryCatalog.Seed().Select(s => s.DisplayName),
            provider.GetRequiredService<IServerDiscoveryService>().GetDiscovered().Select(s => s.DisplayName).Order());
    }

    // ---- history catalogue ---------------------------------------------------------------------------

    [Fact]
    public void History_ScenarioSetIsStructurallyDeterministic()
    {
        var after = DateTimeOffset.UtcNow;
        var scenarios = QaHistoryCatalog.Scenarios;

        Assert.Equal(
            ["Normal", "CPU spike", "Warning", "Critical", "Offline gap", "Recovery", "RAM null", "Empty", "DB unavailable"],
            scenarios.Select(s => s.Label));
        Assert.Equal(Enum.GetValues<QaHistoryKind>(), scenarios.Select(s => s.Kind));
        Assert.Equal(scenarios.Count, scenarios.Select(s => s.Server.Id).Distinct().Count());
        AssertInWindow(QaHistoryCatalog.Now, after);

        var expected = new Dictionary<string, (ServerHealth Health, double? Cpu, double? Mem, double? Disk, bool HasSnapshot)>
        {
            ["Normal"] = (ServerHealth.Healthy, 24, 50, 60, true),
            ["CPU spike"] = (ServerHealth.Healthy, 18, 45, 61, true),
            ["Warning"] = (ServerHealth.Warning, 80, 55, 62, true),
            ["Critical"] = (ServerHealth.Critical, 95, 71, 95, true),
            ["Offline gap"] = (ServerHealth.Offline, 22, 50, 60, true),
            ["Recovery"] = (ServerHealth.Healthy, 24, 50, 60, true),
            ["RAM null"] = (ServerHealth.Healthy, 24, null, 60, true),
            ["Empty"] = (ServerHealth.Unknown, null, null, null, false),
            ["DB unavailable"] = (ServerHealth.Unknown, null, null, null, false)
        };

        for (var i = 0; i < scenarios.Count; i++)
        {
            var scenario = scenarios[i];
            var want = expected[scenario.Label];
            Assert.Equal($"QA · {scenario.Label}", scenario.Server.Name);
            Assert.Equal($"qa-history-{i}.local", scenario.Server.Host);
            Assert.Equal(QaHistoryCatalog.Now.AddSeconds(i), scenario.Server.CreatedAt);
            Assert.Equal(want.Health, scenario.State.Health);
            Assert.Equal(scenario.Server.Id, scenario.State.ServerId);
            Assert.Equal(QaHistoryCatalog.Now, scenario.State.LastSuccessAt);
            Assert.Equal(want.HasSnapshot, scenario.Snapshot is not null);
            if (scenario.Snapshot is { } snapshot)
            {
                Assert.Equal(scenario.Server.Id, snapshot.ServerId);
                Assert.Equal((want.Cpu, want.Mem, want.Disk), (snapshot.CpuUsagePercent, snapshot.MemoryUsagePercent, snapshot.DiskUsagePercent));
            }
        }
    }

    [Fact]
    public void History_SampleGeneratorIsAPureFunctionOfTheWindow()
    {
        var end = QaHistoryCatalog.Now;
        var start = end - TimeSpan.FromHours(24);

        foreach (var scenario in QaHistoryCatalog.Scenarios)
        {
            var once = QaHistoryCatalog.Generate(scenario, start, end);
            var twice = QaHistoryCatalog.Generate(scenario, start, end);
            Assert.Equal(once, twice);

            if (scenario.Kind is QaHistoryKind.Empty or QaHistoryKind.Unavailable)
            {
                Assert.Empty(once);
                continue;
            }

            Assert.Equal(start, once[0].CapturedAtUtc);
            Assert.Equal(end, once[^1].CapturedAtUtc);
            Assert.All(once, sample => Assert.Equal(scenario.Server.Id, sample.ServerId));
        }

        var ramNull = QaHistoryCatalog.Generate(QaHistoryCatalog.Scenarios.Single(s => s.Kind == QaHistoryKind.RamNull), start, end);
        Assert.All(ramNull, sample => Assert.Null(sample.MemoryPercent)); // unknown stays null, never 0

        var gap = QaHistoryCatalog.Generate(QaHistoryCatalog.Scenarios.Single(s => s.Kind == QaHistoryKind.OfflineGap), start, end);
        Assert.Contains(gap, sample => sample.Health == ServerHealth.Offline && sample.CpuPercent is null);
    }

    [Fact]
    public async Task History_QueryServiceIsDeterministicAndUnavailableThrows()
    {
        var service = new QaServerHistoryQueryService();
        foreach (var scenario in QaHistoryCatalog.Scenarios)
        {
            if (scenario.Kind == QaHistoryKind.Unavailable)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetHistoryAsync(scenario.Server.Id, HistoryTimeRange.Last24Hours));
                continue;
            }

            var first = await service.GetHistoryAsync(scenario.Server.Id, HistoryTimeRange.Last24Hours);
            var second = await service.GetHistoryAsync(scenario.Server.Id, HistoryTimeRange.Last24Hours);
            Assert.Equal(QaHistoryCatalog.Now, first.EndUtc);
            Assert.Equal((first.StartUtc, first.EndUtc, first.ContainsOfflineSamples), (second.StartUtc, second.EndUtc, second.ContainsOfflineSamples));
            Assert.Equal(scenario.Kind is QaHistoryKind.OfflineGap or QaHistoryKind.Recovery, first.ContainsOfflineSamples);
        }
    }

    // ---- store-screenshot catalogue ---------------------------------------------------------------------

    [Fact]
    public async Task Screenshot_CatalogueIsStructurallyDeterministicAcrossApplies()
    {
        var runs = new List<IReadOnlyList<(string Name, ServerOperatingSystem Os, string Host, double? Cpu, double? Mem, double? Disk, ServerHealth Health)>>();
        for (var run = 0; run < 2; run++)
        {
            using var provider = Provider("screenshot");
            var after = DateTimeOffset.UtcNow;
            var servers = await provider.GetRequiredService<IServerService>().GetAllAsync();
            var metrics = provider.GetRequiredService<IServerMetricsStore>();
            var states = provider.GetRequiredService<IServerMonitoringStateStore>();

            Assert.Equal(servers.Count, servers.Select(s => s.Id).Distinct().Count());
            runs.Add(servers.Select(server =>
            {
                var snapshot = metrics.GetLastSnapshot(server.Id);
                var state = states.Get(server.Id);
                Assert.NotNull(snapshot);
                AssertInWindow(server.CreatedAt, after);
                AssertInWindow(snapshot!.CollectedAt, after);
                AssertInWindow(state.LastSuccessAt!.Value, after);
                return (server.Name, server.OperatingSystem, server.Host, snapshot.CpuUsagePercent, snapshot.MemoryUsagePercent, snapshot.DiskUsagePercent, state.Health);
            }).ToList());
        }

        Assert.Equal(runs[0], runs[1]);
        Assert.Equal(
            [
                ("Home Server", ServerOperatingSystem.Linux, "10.0.0.20", (double?)18, (double?)46, (double?)63, ServerHealth.Healthy),
                ("Media Server", ServerOperatingSystem.Linux, "10.0.0.21", 34, 58, 71, ServerHealth.Healthy),
                ("Mac Mini", ServerOperatingSystem.MacOS, "10.0.0.22", 12, 42, 37, ServerHealth.Healthy)
            ],
            runs[0]);
    }

    [Fact]
    public async Task Screenshot_DataPlaneIsReadOnly()
    {
        using var provider = Provider("screenshot");
        var servers = provider.GetRequiredService<IServerService>();
        var any = (await servers.GetAllAsync())[0];

        await Assert.ThrowsAsync<NotSupportedException>(() => servers.UpdateAsync(any.Id, null!));
        Assert.False(await servers.RemoveAsync(any.Id));
        var refresh = await provider.GetRequiredService<IServerMetricsStore>().RefreshAsync(any);
        Assert.False(refresh.IsSuccess);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    /// <summary>A catalogue timestamp is fixed once per process: no later than the call, no earlier than start.</summary>
    private static void AssertInWindow(DateTimeOffset value, DateTimeOffset after)
    {
        var processStart = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime()).AddSeconds(-1);
        Assert.InRange(value, processStart, after.AddSeconds(5)); // +5s: the catalogue's fixed small offsets
    }

    private static void AssertQa(object implementation) =>
        Assert.True(
            implementation.GetType().Namespace == typeof(QaMonitoringEngine).Namespace,
            $"{implementation.GetType().FullName} is not a QA double");

    private static ServiceProvider Provider(string harness)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Apply(harness, services);
        return services.BuildServiceProvider();
    }

    private static void Apply(string harness, IServiceCollection services)
    {
        switch (harness)
        {
            case "discovery": QaDiscoveryComposition.Apply(services); break;
            case "history": QaHistoryComposition.Apply(services); break;
            case "screenshot": QaStoreScreenshotComposition.Apply(services); break;
            default: throw new ArgumentOutOfRangeException(nameof(harness), harness, null);
        }
    }

    private static bool IsRequested(string harness) => harness switch
    {
        "discovery" => QaDiscoveryComposition.IsRequested(),
        "history" => QaHistoryComposition.IsRequested(),
        "screenshot" => QaStoreScreenshotComposition.IsRequested(),
        _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, null)
    };

    private static string LaunchFlag(string harness) => harness switch
    {
        "discovery" => QaDiscoveryComposition.LaunchFlag,
        "history" => QaHistoryComposition.LaunchFlag,
        "screenshot" => QaStoreScreenshotComposition.LaunchFlag,
        _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, null)
    };
}
