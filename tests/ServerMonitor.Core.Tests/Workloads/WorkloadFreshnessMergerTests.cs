using ServerMonitor.Core.Workloads;

namespace ServerMonitor.Core.Tests.Workloads;

public sealed class WorkloadFreshnessMergerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0 + TimeSpan.FromMinutes(1);
    private static readonly Guid Id = Guid.NewGuid();

    private static DockerSnapshot Docker(DockerAvailability availability, params string[] names) => new()
    {
        Availability = availability,
        Containers = names.Select(n => new ContainerInfo
        {
            ContainerId = n,
            Name = n,
            Image = "img",
            State = ContainerState.Running,
            StatusText = "Up",
            Health = ContainerHealth.None
        }).ToArray()
    };

    private static ServiceSnapshot Services(WorkloadServiceAvailability availability, ServiceManager manager = ServiceManager.Systemd) => new()
    {
        Manager = manager,
        Availability = availability
    };

    private static ServerWorkloadSnapshot Snapshot(
        DockerSnapshot docker,
        ServiceSnapshot services,
        DateTimeOffset capturedAt) => new()
    {
        ServerId = Id,
        CapturedAtUtc = capturedAt,
        Docker = docker,
        Services = services
    };

    [Fact]
    public void NoPrevious_FreshAttempt_UsedAsIs_NotStale()
    {
        var attempt = Snapshot(Docker(DockerAvailability.Available, "a"), Services(WorkloadServiceAvailability.Available), T0);

        var merged = WorkloadFreshnessMerger.Merge(previous: null, attempt, T1);

        Assert.False(merged.IsStale);
        Assert.Equal(T1, merged.CapturedAtUtc);
        Assert.Equal(T1, merged.LastAttemptAtUtc);
        Assert.Equal(DockerAvailability.Available, merged.Docker.Availability);
        Assert.Single(merged.Docker.Containers);
    }

    [Fact]
    public void BothPartsFail_WithPrevious_CarriesOver_MarksStale_KeepsCaptureTime()
    {
        var previous = Snapshot(Docker(DockerAvailability.Available, "a"), Services(WorkloadServiceAvailability.Available), T0);
        var attempt = Snapshot(Docker(DockerAvailability.Unknown), Services(WorkloadServiceAvailability.Error), T1);

        var merged = WorkloadFreshnessMerger.Merge(previous, attempt, T1);

        Assert.True(merged.IsStale);
        Assert.Equal(T0, merged.CapturedAtUtc);          // never moves forward on a fully-failed attempt
        Assert.Equal(T1, merged.LastAttemptAtUtc);
        Assert.Single(merged.Docker.Containers);          // previous list preserved, not zeroed
        Assert.Equal(WorkloadServiceAvailability.Available, merged.Services.Availability);
    }

    [Fact]
    public void DefinitiveNegative_IsFresh_NotCarriedOver()
    {
        // "Not installed" is a real, current answer — it must replace a previous Available, not be treated
        // as a failure to carry over.
        var previous = Snapshot(Docker(DockerAvailability.Available, "a"), Services(WorkloadServiceAvailability.Available), T0);
        var attempt = Snapshot(Docker(DockerAvailability.NotInstalled), Services(WorkloadServiceAvailability.NotInstalled), T1);

        var merged = WorkloadFreshnessMerger.Merge(previous, attempt, T1);

        Assert.False(merged.IsStale);
        Assert.Equal(T1, merged.CapturedAtUtc);
        Assert.Equal(DockerAvailability.NotInstalled, merged.Docker.Availability);
        Assert.Empty(merged.Docker.Containers);
    }

    [Fact]
    public void OnePartFreshOnePartFailed_MixedMerge_StaleButCaptureAdvances()
    {
        // Docker fresh, services failed: keep prior services (stale), take fresh Docker, advance capture.
        var previous = Snapshot(Docker(DockerAvailability.Available, "old"), Services(WorkloadServiceAvailability.Available), T0);
        var attempt = Snapshot(Docker(DockerAvailability.Available, "new"), Services(WorkloadServiceAvailability.Unknown), T1);

        var merged = WorkloadFreshnessMerger.Merge(previous, attempt, T1);

        Assert.True(merged.IsStale);                      // services carried over
        Assert.Equal(T1, merged.CapturedAtUtc);           // something fresh was shown
        Assert.Equal("new", merged.Docker.Containers[0].Name);
        Assert.Equal(WorkloadServiceAvailability.Available, merged.Services.Availability);
    }

    [Fact]
    public void FailedAttempt_NoPrevious_NotStale_NothingToCarry()
    {
        var attempt = Snapshot(Docker(DockerAvailability.Unknown), Services(WorkloadServiceAvailability.Unknown), T1);

        var merged = WorkloadFreshnessMerger.Merge(previous: null, attempt, T1);

        Assert.False(merged.IsStale);
        Assert.Equal(T1, merged.CapturedAtUtc);
        Assert.Equal(DockerAvailability.Unknown, merged.Docker.Availability);
    }
}
