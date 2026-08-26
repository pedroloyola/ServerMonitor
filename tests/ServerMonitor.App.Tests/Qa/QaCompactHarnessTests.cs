using ServerMonitor.App.Qa;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// The Debug-only compact-widget harness (--qa-compact[:N]) seeds a variable-length catalog so the
/// real compact card can be inspected at any server count and across every health state without a
/// live desktop. These tests verify the harness is off by default and that its catalog is correct;
/// like the M6 health harness they support, not replace, the on-screen compact QA.
/// </summary>
public sealed class QaCompactHarnessTests
{
    [Fact]
    public void HarnessIsNotRequestedByDefault()
    {
        Assert.False(QaCompactComposition.IsRequested());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(20)]
    public void Build_ProducesTheRequestedNumberOfServers(int count)
    {
        var catalog = QaCompactCatalog.Build(count);

        Assert.Equal(count, catalog.Servers.Count);
        Assert.Equal(count, catalog.Scenarios.Count);
        Assert.All(catalog.Scenarios, scenario => Assert.Equal(scenario.Server.Id, scenario.State.ServerId));
    }

    [Fact]
    public void Build_ClampsAbsurdCounts()
    {
        Assert.Empty(QaCompactCatalog.Build(-5).Servers);
        Assert.Equal(40, QaCompactCatalog.Build(1000).Servers.Count);
    }

    [Fact]
    public void Build_CyclesThroughHealthStates()
    {
        var catalog = QaCompactCatalog.Build(8);
        var healths = catalog.Scenarios.Select(scenario => scenario.State.Health).ToList();

        Assert.Contains(ServerHealth.Healthy, healths);
        Assert.Contains(ServerHealth.Warning, healths);
        Assert.Contains(ServerHealth.Critical, healths);
        Assert.Contains(ServerHealth.Offline, healths);
        Assert.Contains(ServerHealth.Unknown, healths);
    }

    [Fact]
    public void Build_KeepsUnknownMetricAbsentNotZero()
    {
        // The eighth bucket is the "partial" scenario: memory unknown must stay null, never 0.
        var catalog = QaCompactCatalog.Build(8);
        var partial = catalog.Scenarios[7];

        Assert.NotNull(partial.Snapshot);
        Assert.Null(partial.Snapshot!.MemoryUsagePercent);
        Assert.NotNull(partial.Snapshot.CpuUsagePercent);
    }
}
