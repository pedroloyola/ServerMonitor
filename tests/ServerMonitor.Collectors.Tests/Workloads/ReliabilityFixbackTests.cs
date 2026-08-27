using ServerMonitor.Collectors.Workloads.Mapping;
using ServerMonitor.Collectors.Workloads.Parsing;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Workloads;
using ServerMonitor.Infrastructure.Collectors.Workloads;

namespace ServerMonitor.Collectors.Tests.Workloads;

/// <summary>
/// Deterministic regressions for the reliability fixback findings H-03 (malformed ≠ empty; minimal
/// identity) and H-04 (launchd second column is not a reliable exit status). H-01 (Auto OS resolution) is
/// covered in <see cref="WorkloadCollectorTests"/>.
/// </summary>
public sealed class ReliabilityFixbackTests
{
    private static RemoteCommandOutcome Ok(string stdout) =>
        new() { WasExecuted = true, ExitStatus = 0, StandardOutput = stdout, StandardError = string.Empty };

    // ---- H-03: Docker ----

    [Fact]
    public void Docker_all_malformed_input_is_unrecognized_not_empty()
    {
        var result = DockerPsJsonParser.Parse("not json\nalso not json\n{ broken");

        Assert.Empty(result.Containers);
        Assert.True(result.HadInput);
        Assert.True(result.IsUnrecognized);
    }

    [Fact]
    public void Docker_schema_invalid_row_without_identity_is_not_materialized()
    {
        // Valid JSON, but the id is the wrong type: it is not a container and must not become an empty row.
        var result = DockerPsJsonParser.Parse(
            """{"ID":42,"Names":null,"Image":{},"State":"running","Status":"Up"}""");

        Assert.Empty(result.Containers);
        Assert.True(result.IsUnrecognized);
        Assert.Equal(1, result.MalformedCount);
    }

    [Fact]
    public void Docker_empty_input_is_not_unrecognized()
    {
        var result = DockerPsJsonParser.Parse("");

        Assert.False(result.HadInput);
        Assert.False(result.IsUnrecognized);
    }

    [Fact]
    public void Docker_one_bad_record_among_good_is_tolerated()
    {
        var input = string.Join('\n',
            """{"ID":"a","Names":"one","Image":"i","State":"running","Status":"Up"}""",
            """{"ID":7}""",
            """{"ID":"b","Names":"two","Image":"i","State":"exited","Status":"Exited (0)"}""");

        var result = DockerPsJsonParser.Parse(input);

        Assert.Equal(2, result.Containers.Count);
        Assert.False(result.IsUnrecognized);
        Assert.Equal(1, result.MalformedCount);
    }

    [Fact]
    public void Docker_mapper_maps_all_malformed_listing_to_error()
    {
        var snapshot = DockerWorkloadMapper.Map(Ok("27.0.3"), Ok("garbage\nmore garbage\n{ broken"));

        Assert.Equal(DockerAvailability.Error, snapshot.Availability);
        Assert.Empty(snapshot.Containers);
    }

    [Fact]
    public void Docker_mapper_empty_listing_stays_available_empty()
    {
        var snapshot = DockerWorkloadMapper.Map(Ok("27.0.3"), Ok(string.Empty));

        Assert.Equal(DockerAvailability.Available, snapshot.Availability);
        Assert.Empty(snapshot.Containers);
    }

    // ---- H-03: services ----

    [Fact]
    public void Systemd_all_non_service_lines_is_unrecognized()
    {
        var result = SystemdServicesParser.Parse("garbage one\nnonsense two three four\n", null);

        Assert.Empty(result.Services);
        Assert.True(result.HadInput);
        Assert.True(result.IsUnrecognized);
    }

    [Fact]
    public void Systemd_mapper_maps_all_malformed_listing_to_error()
    {
        var units = Ok("garbage line one\nanother bad line here");

        var snapshot = ServiceWorkloadMapper.Map(ServerOperatingSystem.Linux, units, null, null);

        Assert.Equal(ServiceManager.Systemd, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.Error, snapshot.Availability);
    }

    [Fact]
    public void Launchd_block_of_only_malformed_rows_is_unrecognized()
    {
        var result = LaunchdPrintSystemParser.Parse("system = {\n\tservices = {\n\t\tnot-a-row\n\t\t123 0\n\t}\n}");

        Assert.Empty(result.Services);
        Assert.True(result.HadInput);
        Assert.True(result.IsUnrecognized);
    }

    [Fact]
    public void Launchd_mapper_maps_all_malformed_block_to_error()
    {
        var print = Ok("system = {\n\tservices = {\n\t\tnot-a-row\n\t\t123 0\n\t}\n}");

        var snapshot = ServiceWorkloadMapper.Map(ServerOperatingSystem.MacOS, null, null, print);

        Assert.Equal(ServiceManager.Launchd, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.Error, snapshot.Availability);
    }

    [Fact]
    public void Launchd_empty_input_is_not_unrecognized()
    {
        var result = LaunchdPrintSystemParser.Parse(string.Empty);

        Assert.False(result.HadInput);
        Assert.False(result.IsUnrecognized);
    }

    // ---- H-04: launchd runtime state (finalized against the real macOS 26.6 dump) ----

    [Fact]
    public void Launchd_not_running_with_nonzero_last_exit_is_stopped_not_failed()
    {
        // The last-exit token (0/1 on the real host) is not elevated to Failed: legitimate one-shot jobs
        // exit non-zero by design, and the summary can't tell that from a real failure.
        var result = LaunchdPrintSystemParser.Parse(
            "system = {\n\tservices = {\n" +
            "\t\t0 1 com.example.one-shot\n" +
            "\t\t- 78 com.example.stopped-nonzero\n" +
            "\t}\n}");

        Assert.Equal(2, result.Services.Count);
        Assert.All(result.Services, s => Assert.Equal(ServiceState.Stopped, s.State));
    }

    [Fact]
    public void Launchd_running_pid_is_running_regardless_of_second_column()
    {
        // Even an unexpected second-column token on some macOS version cannot flip a running daemon:
        // state is derived from the PID alone.
        var result = LaunchdPrintSystemParser.Parse(
            "system = {\n\tservices = {\n\t\t4321 (pe) com.example.daemon\n\t}\n}");

        Assert.Equal(ServiceState.Running, Assert.Single(result.Services).State);
    }
}
