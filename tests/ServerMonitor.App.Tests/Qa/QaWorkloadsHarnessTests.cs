using ServerMonitor.App.Qa;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// The Debug-only workloads harness (--qa-workloads) seeds one server per Docker/services shape so the
/// real read-only workload UI can be inspected across every case without SSH or a Docker host. These
/// tests verify it is off by default, that its catalog is internally consistent and covers the required
/// shapes, that bounds/truncation are honored, and — critically — that hostile names are neutralized by
/// sanitization before they ever reach the UI. Like the other harnesses, they support (not replace) the
/// on-screen visual QA.
/// </summary>
public sealed class QaWorkloadsHarnessTests
{
    private static QaWorkloadScenario ByLabel(string label) =>
        QaWorkloadsCatalog.Scenarios.Single(s => s.Label == label);

    [Fact]
    public void HarnessIsNotRequestedByDefault()
    {
        Assert.False(QaWorkloadsComposition.IsRequested());
    }

    [Fact]
    public void Catalog_IsInternallyConsistent()
    {
        Assert.NotEmpty(QaWorkloadsCatalog.Scenarios);
        Assert.Equal(QaWorkloadsCatalog.Scenarios.Count, QaWorkloadsCatalog.Servers.Count);
        Assert.All(QaWorkloadsCatalog.Scenarios, scenario =>
        {
            Assert.Equal(scenario.Server.Id, scenario.Workload.ServerId);
            Assert.Equal(scenario.Server.Id, scenario.State.ServerId);
            Assert.Equal(scenario.Server.Id, scenario.Metrics.ServerId);
        });
    }

    [Fact]
    public void Catalog_CoversDockerAvailabilities()
    {
        var availabilities = QaWorkloadsCatalog.Scenarios.Select(s => s.Workload.Docker.Availability).ToHashSet();

        Assert.Contains(DockerAvailability.NotInstalled, availabilities);
        Assert.Contains(DockerAvailability.PermissionDenied, availabilities);
        Assert.Contains(DockerAvailability.Unavailable, availabilities);
        Assert.Contains(DockerAvailability.Available, availabilities);
        Assert.Contains(DockerAvailability.Unknown, availabilities);
    }

    [Fact]
    public void Catalog_CoversServiceManagers()
    {
        var managers = QaWorkloadsCatalog.Scenarios.Select(s => s.Workload.Services.Manager).ToHashSet();

        Assert.Contains(ServiceManager.Systemd, managers);
        Assert.Contains(ServiceManager.Launchd, managers);
        Assert.Contains(ServiceManager.Unsupported, managers);
    }

    [Fact]
    public void Catalog_HasLargeListsAndTruncation()
    {
        Assert.Equal(500, ByLabel("Docker: 500 containers").Workload.Docker.Containers.Count);
        Assert.Equal(2000, ByLabel("services: 2000 units").Workload.Services.Services.Count);

        var truncatedDocker = ByLabel("Docker: truncated (>cap)").Workload.Docker;
        Assert.True(truncatedDocker.Truncated);
        Assert.True(truncatedDocker.Containers.Count <= WorkloadLimits.MaxContainers);

        var truncatedServices = ByLabel("services: truncated (>cap)").Workload.Services;
        Assert.True(truncatedServices.Truncated);
        Assert.True(truncatedServices.Services.Count <= WorkloadLimits.MaxServices);
    }

    [Fact]
    public void Catalog_HasStaleAndAllUnknownShapes()
    {
        Assert.True(ByLabel("Stale (carried over)").Workload.IsStale);

        var unknown = ByLabel("All unknown (probe failed)").Workload;
        Assert.Equal(DockerAvailability.Unknown, unknown.Docker.Availability);
        Assert.Equal(WorkloadServiceAvailability.Unknown, unknown.Services.Availability);
        Assert.False(unknown.IsStale); // a fresh attempt that determined nothing is not "stale"
    }

    [Fact]
    public void HostileNames_AreNeutralizedBeforeReachingTheUi()
    {
        var hostile = ByLabel("Hostile names (sanitized)").Workload;

        var texts = hostile.Docker.Containers
            .SelectMany(c => new[] { c.ContainerId, c.Name, c.Image, c.StatusText })
            .Concat(hostile.Services.Services.SelectMany(s => new[] { s.Id, s.Name, s.DisplayName, s.SubState }))
            .Where(t => t is not null)
            .Cast<string>()
            .ToList();

        Assert.NotEmpty(texts);
        Assert.All(texts, text =>
        {
            Assert.DoesNotContain('\n', text);
            Assert.DoesNotContain('\t', text);
            Assert.DoesNotContain('', text);          // ANSI escape
            Assert.DoesNotContain('‮', text);          // bidi override
            Assert.True(text.Length <= WorkloadLimits.MaxTextLength);
        });
    }
}
