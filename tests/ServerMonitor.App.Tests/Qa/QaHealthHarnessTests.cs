using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// The Debug-only visual health QA harness feeds the real <see cref="ServerCardViewModel"/> a
/// deterministic catalog of monitoring states. These tests verify the harness is off by default
/// and that each scenario maps to the health/flags the card is meant to render — in particular
/// that a partial snapshot keeps an unknown metric absent (never zero). This is the harness's
/// verification for M6 §8: it does not replace the on-screen visual QA, but it guarantees the
/// data the card renders is correct without a live desktop.
/// </summary>
public sealed class QaHealthHarnessTests
{
    private static readonly Func<Task> NoOp = () => Task.CompletedTask;

    private static ServerCardViewModel Card(string label, ServerOperatingSystem os)
    {
        var scenario = QaHealthCatalog.Scenarios.Single(
            candidate => candidate.Label == label && candidate.Server.OperatingSystem == os);

        var stateStore = new ServerMonitoringStateStore();
        foreach (var seeded in QaHealthCatalog.Scenarios)
        {
            stateStore.Set(seeded.State);
        }

        return new ServerCardViewModel(
            scenario.Server,
            connectionResult: null,
            new FakeLocalizationService(),
            new QaMetricsStore(),
            new FakeConnectionStateStore(),
            stateStore,
            new QaMonitoringEngine(),
            NoOp,
            NoOp,
            NoOp);
    }

    [Fact]
    public void HarnessIsNotRequestedByDefault()
    {
        // The test host is launched without the flag, so the real engine path stays active.
        Assert.False(QaHealthComposition.IsRequested());
    }

    [Fact]
    public void CatalogCoversEveryScenarioForBothOperatingSystems()
    {
        var expected = new[] { "Healthy", "Warning", "Critical", "Offline", "Stale", "Unknown", "Refreshing", "Partial" };

        foreach (var os in new[] { ServerOperatingSystem.Linux, ServerOperatingSystem.MacOS })
        {
            var labels = QaHealthCatalog.Scenarios
                .Where(scenario => scenario.Server.OperatingSystem == os)
                .Select(scenario => scenario.Label);
            Assert.Equal(expected.OrderBy(label => label), labels.OrderBy(label => label));
        }

        Assert.Equal(16, QaHealthCatalog.Scenarios.Count);
        Assert.All(QaHealthCatalog.Scenarios, scenario => Assert.Equal(scenario.Server.Id, scenario.State.ServerId));
    }

    [Theory]
    [InlineData(ServerOperatingSystem.Linux)]
    [InlineData(ServerOperatingSystem.MacOS)]
    public void HealthyScenario_RendersHealthyWithAllMetrics(ServerOperatingSystem os)
    {
        var card = Card("Healthy", os);

        Assert.Equal(ServerHealth.Healthy, card.Health);
        Assert.True(card.HasMetrics);
        Assert.True(card.HasCpuPercent);
        Assert.Equal(22, card.CpuUsageValue);
        Assert.True(card.HasMemoryPercent);
        Assert.True(card.HasDiskPercent);
        Assert.False(card.IsStale);
        Assert.False(card.IsRefreshingMetrics);
        Assert.False(card.HasStaleIndicator);
    }

    [Theory]
    [InlineData(ServerOperatingSystem.Linux)]
    [InlineData(ServerOperatingSystem.MacOS)]
    public void WarningAndCriticalScenarios_RenderTheirHealth(ServerOperatingSystem os)
    {
        var warning = Card("Warning", os);
        Assert.Equal(ServerHealth.Warning, warning.Health);
        Assert.Equal(84, warning.CpuUsageValue);

        var critical = Card("Critical", os);
        Assert.Equal(ServerHealth.Critical, critical.Health);
        Assert.Equal(93, critical.DiskUsageValue);
    }

    [Theory]
    [InlineData(ServerOperatingSystem.Linux)]
    [InlineData(ServerOperatingSystem.MacOS)]
    public void OfflineScenario_KeepsRetainedMetricsAndSurfacesNoError(ServerOperatingSystem os)
    {
        var card = Card("Offline", os);

        Assert.Equal(ServerHealth.Offline, card.Health);
        Assert.True(card.HasMetrics); // prior snapshot retained
        Assert.False(card.HasMetricsError); // an error banner is suppressed while a snapshot exists
        Assert.Equal(4, card.ConsecutiveFailures);
        Assert.Equal(MetricsCollectionErrorCode.ConnectionFailed, card.LastError);
    }

    [Theory]
    [InlineData(ServerOperatingSystem.Linux)]
    [InlineData(ServerOperatingSystem.MacOS)]
    public void StaleScenario_ShowsStaleIndicator(ServerOperatingSystem os)
    {
        var card = Card("Stale", os);

        Assert.True(card.IsStale);
        Assert.True(card.HasMetrics);
        Assert.True(card.HasStaleIndicator);
        Assert.NotNull(card.StaleAgeDisplay);
    }

    [Theory]
    [InlineData(ServerOperatingSystem.Linux)]
    [InlineData(ServerOperatingSystem.MacOS)]
    public void UnknownScenario_IsPendingNotErrored(ServerOperatingSystem os)
    {
        var card = Card("Unknown", os);

        Assert.Equal(ServerHealth.Unknown, card.Health);
        Assert.False(card.HasMetrics);
        Assert.True(card.IsMetricsPending);
        Assert.False(card.HasMetricsError);
        Assert.False(card.IsRefreshingMetrics);
    }

    [Theory]
    [InlineData(ServerOperatingSystem.Linux)]
    [InlineData(ServerOperatingSystem.MacOS)]
    public void RefreshingScenario_ShowsRefreshIndicator(ServerOperatingSystem os)
    {
        var card = Card("Refreshing", os);

        Assert.True(card.IsRefreshingMetrics);
        Assert.True(card.HasMetrics);
    }

    [Theory]
    [InlineData(ServerOperatingSystem.Linux)]
    [InlineData(ServerOperatingSystem.MacOS)]
    public void PartialScenario_KeepsUnknownMetricAbsentNotZero(ServerOperatingSystem os)
    {
        var card = Card("Partial", os);

        // CPU and disk are known...
        Assert.True(card.HasCpuPercent);
        Assert.Equal(12, card.CpuUsageValue);
        Assert.True(card.HasDiskPercent);

        // ...memory is unknown: it must render as absent, never as 0 (unknown ≠ zero).
        Assert.False(card.HasMemoryPercent);
        Assert.False(card.HasMemoryUsage);
        Assert.Null(card.MemoryUsageDisplay);
    }
}
