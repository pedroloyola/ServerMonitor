using ServerMonitor.Collectors.Workloads.Mapping;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Workloads;
using ServerMonitor.Infrastructure.Collectors.Workloads;

namespace ServerMonitor.Collectors.Tests.Workloads.Mapping;

public sealed class ServiceWorkloadMapperTests
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
    public void Linux_systemd_available_parses_services()
    {
        var units = Outcome(0, stdout: "ssh.service loaded active running OpenBSD Secure Shell server");
        var unitFiles = Outcome(0, stdout: "ssh.service enabled enabled");

        var snapshot = ServiceWorkloadMapper.Map(ServerOperatingSystem.Linux, units, unitFiles, null);

        Assert.Equal(ServiceManager.Systemd, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.Available, snapshot.Availability);
        var service = Assert.Single(snapshot.Services);
        Assert.Equal("ssh.service", service.Id);
        Assert.Equal(ServiceStartupState.Enabled, service.StartupState);
    }

    [Fact]
    public void Linux_unit_files_failure_keeps_runtime_inventory_and_unknown_startup()
    {
        var units = Outcome(0, stdout: "ssh.service loaded active running OpenBSD Secure Shell server");
        var unitFiles = Outcome(1, stderr: "transient failure");

        var snapshot = ServiceWorkloadMapper.Map(ServerOperatingSystem.Linux, units, unitFiles, null);

        Assert.Equal(ServiceManager.Systemd, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.Available, snapshot.Availability);
        Assert.Null(Assert.Single(snapshot.Services).StartupState);
    }

    [Fact]
    public void Linux_systemctl_missing_is_not_installed_and_unsupported()
    {
        var units = Outcome(127, stderr: "bash: systemctl: command not found");

        var snapshot = ServiceWorkloadMapper.Map(ServerOperatingSystem.Linux, units, null, null);

        Assert.Equal(ServiceManager.Unsupported, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.NotInstalled, snapshot.Availability);
        Assert.Empty(snapshot.Services);
    }

    [Fact]
    public void Linux_systemd_not_pid1_is_unavailable_and_unsupported()
    {
        var units = Outcome(1, stderr: "System has not been booted with systemd as init system (PID 1). Can't operate.");

        var snapshot = ServiceWorkloadMapper.Map(ServerOperatingSystem.Linux, units, null, null);

        Assert.Equal(ServiceManager.Unsupported, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.Unavailable, snapshot.Availability);
    }

    [Fact]
    public void Linux_permission_denied_keeps_systemd_manager()
    {
        var units = Outcome(1, stderr: "Failed to list units: Access denied");

        var snapshot = ServiceWorkloadMapper.Map(ServerOperatingSystem.Linux, units, null, null);

        Assert.Equal(ServiceManager.Systemd, snapshot.Manager); // systemd is present, we just cannot read
        Assert.Equal(WorkloadServiceAvailability.PermissionDenied, snapshot.Availability);
    }

    [Fact]
    public void Linux_not_probed_is_unknown()
    {
        var snapshot = ServiceWorkloadMapper.Map(ServerOperatingSystem.Linux, null, null, null);

        Assert.Equal(ServiceManager.Unsupported, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.Unknown, snapshot.Availability);
    }

    [Fact]
    public void Linux_oversized_systemd_output_is_error()
    {
        var snapshot = ServiceWorkloadMapper.Map(
            ServerOperatingSystem.Linux,
            Outcome(null, stdout: null, overLimit: true),
            null,
            null);

        Assert.Equal(ServiceManager.Systemd, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.Error, snapshot.Availability);
        Assert.Empty(snapshot.Services);
    }

    [Fact]
    public void MacOs_launchd_available_parses_services()
    {
        var print = Outcome(0, stdout: "system = {\n\tservices = {\n\t\t123 0 com.apple.sshd\n\t}\n}");

        var snapshot = ServiceWorkloadMapper.Map(ServerOperatingSystem.MacOS, null, null, print);

        Assert.Equal(ServiceManager.Launchd, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.Available, snapshot.Availability);
        var service = Assert.Single(snapshot.Services);
        Assert.Equal("com.apple.sshd", service.Id);
        Assert.Equal(ServiceState.Running, service.State);
    }

    [Fact]
    public void MacOs_system_domain_permission_error_is_permission_denied()
    {
        // R2: launchctl print system may be root-only; a domain/IO error is typed, never an empty list.
        var print = Outcome(1, stderr: "Could not print domain: 5: Input/output error");

        var snapshot = ServiceWorkloadMapper.Map(ServerOperatingSystem.MacOS, null, null, print);

        Assert.Equal(ServiceManager.Launchd, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.PermissionDenied, snapshot.Availability);
        Assert.Empty(snapshot.Services);
    }

    [Fact]
    public void MacOs_not_probed_is_unknown_launchd()
    {
        var snapshot = ServiceWorkloadMapper.Map(ServerOperatingSystem.MacOS, null, null, null);

        Assert.Equal(ServiceManager.Launchd, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.Unknown, snapshot.Availability);
    }

    [Fact]
    public void MacOs_launchctl_missing_is_not_installed()
    {
        var snapshot = ServiceWorkloadMapper.Map(
            ServerOperatingSystem.MacOS,
            null,
            null,
            Outcome(127, stderr: "launchctl: command not found"));

        Assert.Equal(ServiceManager.Launchd, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.NotInstalled, snapshot.Availability);
    }

    [Fact]
    public void MacOs_oversized_launchd_output_is_error()
    {
        var snapshot = ServiceWorkloadMapper.Map(
            ServerOperatingSystem.MacOS,
            null,
            null,
            Outcome(null, stdout: null, overLimit: true));

        Assert.Equal(ServiceManager.Launchd, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.Error, snapshot.Availability);
        Assert.Empty(snapshot.Services);
    }

    [Fact]
    public void MacOs_unknown_nonzero_exit_is_error()
    {
        var snapshot = ServiceWorkloadMapper.Map(
            ServerOperatingSystem.MacOS,
            null,
            null,
            Outcome(64, stderr: "unexpected launchctl failure"));

        Assert.Equal(ServiceManager.Launchd, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.Error, snapshot.Availability);
    }

    [Theory]
    [InlineData(ServerOperatingSystem.Unknown)]
    [InlineData(ServerOperatingSystem.Auto)]
    public void Unsupported_os_is_unsupported_and_unknown(ServerOperatingSystem os)
    {
        var snapshot = ServiceWorkloadMapper.Map(os, null, null, null);

        Assert.Equal(ServiceManager.Unsupported, snapshot.Manager);
        Assert.Equal(WorkloadServiceAvailability.Unknown, snapshot.Availability);
    }

    [Fact]
    public void MacOs_successful_dump_with_permission_words_in_labels_is_not_denied()
    {
        // L-1 regression: a legitimate, large successful dump whose service labels/paths contain
        // "permission" / "not permitted" must NOT be misread as PermissionDenied — that would mask the
        // whole inventory. Availability comes from exit status + stderr, never from success stdout.
        var rows = string.Join('\n', Enumerable.Range(0, 300)
            .Select(i => $"\t\t{i + 1} 0 com.example.permission-helper-{i}"));
        var stdout = "system = {\n\tservices = {\n" + rows +
                     "\n\t\t9001 0 com.example.operation-not-permitted-watcher\n\t}\n}";

        var snapshot = ServiceWorkloadMapper.Map(ServerOperatingSystem.MacOS, null, null, Outcome(0, stdout: stdout));

        Assert.Equal(WorkloadServiceAvailability.Available, snapshot.Availability);
        Assert.Equal(301, snapshot.Services.Count);
        Assert.Contains(snapshot.Services, s => s.Id == "com.example.operation-not-permitted-watcher");
    }

    [Fact]
    public void MacOs_real_domain_error_on_stderr_is_permission_denied()
    {
        // The counterpart to the regression above: a genuine failure (non-zero exit + domain error on
        // stderr) is still typed as PermissionDenied.
        var print = Outcome(1, stdout: string.Empty, stderr: "Could not print domain: 5: Input/output error");

        var snapshot = ServiceWorkloadMapper.Map(ServerOperatingSystem.MacOS, null, null, print);

        Assert.Equal(WorkloadServiceAvailability.PermissionDenied, snapshot.Availability);
        Assert.Empty(snapshot.Services);
    }

    [Fact]
    public void Linux_successful_units_with_permission_words_in_description_is_available()
    {
        // Same invariant for systemd: an availability signal never comes from a successful stdout, so a
        // description that happens to contain "permission denied" does not flip the whole view.
        var units = Outcome(0, stdout: "vault.service loaded active running Handles permission denied audit logging");

        var snapshot = ServiceWorkloadMapper.Map(ServerOperatingSystem.Linux, units, null, null);

        Assert.Equal(WorkloadServiceAvailability.Available, snapshot.Availability);
        Assert.Single(snapshot.Services);
    }
}
