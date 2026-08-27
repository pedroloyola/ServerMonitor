using ServerMonitor.Collectors.Workloads.Mapping;
using ServerMonitor.Core.Workloads;
using ServerMonitor.Infrastructure.Collectors.Workloads;

namespace ServerMonitor.Collectors.Tests.Workloads.Mapping;

public sealed class DockerWorkloadMapperTests
{
    private static RemoteCommandOutcome Outcome(int? exit, string? stdout = "", string? stderr = "", bool overLimit = false) =>
        new()
        {
            WasExecuted = true,
            ExitStatus = exit,
            StandardOutput = stdout,
            StandardError = stderr,
            OutputExceededLimit = overLimit
        };

    [Fact]
    public void Not_probed_is_unknown()
    {
        Assert.Equal(DockerAvailability.Unknown, DockerWorkloadMapper.Map(null, null).Availability);
        Assert.Equal(DockerAvailability.Unknown, DockerWorkloadMapper.Map(RemoteCommandOutcome.NotExecuted, null).Availability);
    }

    [Fact]
    public void Exit_127_is_not_installed()
    {
        var snapshot = DockerWorkloadMapper.Map(Outcome(127, stderr: "bash: docker: command not found"), null);

        Assert.Equal(DockerAvailability.NotInstalled, snapshot.Availability);
        Assert.Empty(snapshot.Containers);
    }

    [Fact]
    public void Command_not_found_signal_is_not_installed_even_with_nonstandard_exit()
    {
        var snapshot = DockerWorkloadMapper.Map(
            Outcome(1, stderr: "/bin/sh: docker: not found"),
            null);

        Assert.Equal(DockerAvailability.NotInstalled, snapshot.Availability);
    }

    [Fact]
    public void Permission_denied_stderr_is_permission_denied()
    {
        var stderr = "Got permission denied while trying to connect to the Docker daemon socket";
        var snapshot = DockerWorkloadMapper.Map(Outcome(1, stderr: stderr), null);

        Assert.Equal(DockerAvailability.PermissionDenied, snapshot.Availability);
    }

    [Fact]
    public void Daemon_unreachable_stderr_is_unavailable()
    {
        var stderr = "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?";
        var snapshot = DockerWorkloadMapper.Map(Outcome(1, stderr: stderr), null);

        Assert.Equal(DockerAvailability.Unavailable, snapshot.Availability);
    }

    [Fact]
    public void Version_ok_and_ps_ok_is_available_with_containers()
    {
        var ps = Outcome(0, stdout: """{"ID":"id","Names":"web","Image":"nginx","State":"running","Status":"Up (healthy)"}""");
        var snapshot = DockerWorkloadMapper.Map(Outcome(0, stdout: "27.0.3"), ps);

        Assert.Equal(DockerAvailability.Available, snapshot.Availability);
        var container = Assert.Single(snapshot.Containers);
        Assert.Equal("web", container.Name);
        Assert.Equal(ContainerHealth.Healthy, container.Health);
    }

    [Fact]
    public void Version_ok_and_empty_ps_is_available_with_empty_inventory()
    {
        var snapshot = DockerWorkloadMapper.Map(Outcome(0, stdout: "27.0.3"), Outcome(0, stdout: string.Empty));

        Assert.Equal(DockerAvailability.Available, snapshot.Availability);
        Assert.Empty(snapshot.Containers);
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public void Version_ok_but_ps_failed_is_error()
    {
        var snapshot = DockerWorkloadMapper.Map(Outcome(0, stdout: "27.0.3"), Outcome(1, stderr: "transient"));

        Assert.Equal(DockerAvailability.Error, snapshot.Availability);
    }

    [Fact]
    public void Version_ok_and_ps_missing_is_error()
    {
        var snapshot = DockerWorkloadMapper.Map(Outcome(0, stdout: "27.0.3"), null);

        Assert.Equal(DockerAvailability.Error, snapshot.Availability);
    }

    [Fact]
    public void Version_ok_but_ps_was_not_executed_is_error()
    {
        var snapshot = DockerWorkloadMapper.Map(
            Outcome(0, stdout: "27.0.3"),
            RemoteCommandOutcome.NotExecuted);

        Assert.Equal(DockerAvailability.Error, snapshot.Availability);
    }

    [Fact]
    public void Version_ok_but_ps_output_is_undecodable_is_error()
    {
        var snapshot = DockerWorkloadMapper.Map(
            Outcome(0, stdout: "27.0.3"),
            Outcome(0, stdout: null));

        Assert.Equal(DockerAvailability.Error, snapshot.Availability);
    }

    [Fact]
    public void Incomplete_version_probe_is_error()
    {
        // WasExecuted=true + no exit status models a command that started but did not complete
        // (timeout/capped transport path); it must never look Available.
        var snapshot = DockerWorkloadMapper.Map(Outcome(null, stdout: null), null);

        Assert.Equal(DockerAvailability.Error, snapshot.Availability);
        Assert.Empty(snapshot.Containers);
    }

    [Fact]
    public void Oversized_version_output_is_error()
    {
        var snapshot = DockerWorkloadMapper.Map(Outcome(null, stdout: null, overLimit: true), null);

        Assert.Equal(DockerAvailability.Error, snapshot.Availability);
    }

    [Fact]
    public void Unknown_nonzero_exit_without_known_stderr_is_error()
    {
        var snapshot = DockerWorkloadMapper.Map(Outcome(2, stderr: "something odd"), null);

        Assert.Equal(DockerAvailability.Error, snapshot.Availability);
    }
}
