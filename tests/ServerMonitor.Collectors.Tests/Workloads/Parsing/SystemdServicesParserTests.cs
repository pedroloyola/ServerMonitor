using ServerMonitor.Collectors.Workloads.Parsing;
using ServerMonitor.Collectors.Tests.Workloads;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.Collectors.Tests.Workloads.Parsing;

public sealed class SystemdServicesParserTests
{
    private const string ListUnits =
        "ssh.service        loaded active   running OpenBSD Secure Shell server\n" +
        "cron.service       loaded active   running Regular background program processing daemon\n" +
        "nginx.service      loaded failed   failed  A high performance web server\n" +
        "apt-daily.service  loaded inactive dead    Daily apt download activities\n" +
        "oneshot.service    loaded active   exited  Run once and finish";

    private const string ListUnitFiles =
        "ssh.service        enabled  enabled\n" +
        "nginx.service      disabled enabled\n" +
        "apt-daily.service  static   -\n" +
        "oneshot.service    masked   -";

    [Fact]
    public void Parse_reads_unit_id_name_description_and_substate()
    {
        var services = SystemdServicesParser.Parse(ListUnits, ListUnitFiles).Services;

        var ssh = services.Single(s => s.Id == "ssh.service");
        Assert.Equal("ssh", ssh.Name);
        Assert.Equal("OpenBSD Secure Shell server", ssh.DisplayName);
        Assert.Equal("running", ssh.SubState);
    }

    [Fact]
    public void Parse_representative_fixture_covers_runtime_states()
    {
        var result = SystemdServicesParser.Parse(
            FixtureText.Read("systemd-list-units.txt"),
            FixtureText.Read("systemd-list-unit-files.txt"));

        Assert.Equal(6, result.Services.Count);
        Assert.False(result.Truncated);
        Assert.Equal(ServiceState.Running, result.Services.Single(s => s.Id == "ssh.service").State);
        Assert.Equal(ServiceState.Failed, result.Services.Single(s => s.Id == "nginx.service").State);
        Assert.Equal(ServiceState.Stopped, result.Services.Single(s => s.Id == "apt-daily.service").State);
        Assert.Equal(ServiceState.Starting, result.Services.Single(s => s.Id == "worker.service").State);
        Assert.Equal(ServiceState.Stopping, result.Services.Single(s => s.Id == "drain.service").State);
        Assert.Equal(ServiceState.Running, result.Services.Single(s => s.Id == "reload.service").State);
    }

    [Theory]
    [InlineData("enabled", ServiceStartupState.Enabled)]
    [InlineData("enabled-runtime", ServiceStartupState.Enabled)]
    [InlineData("disabled", ServiceStartupState.Disabled)]
    [InlineData("static", ServiceStartupState.Static)]
    [InlineData("indirect", ServiceStartupState.Static)]
    [InlineData("generated", ServiceStartupState.Static)]
    [InlineData("transient", ServiceStartupState.Static)]
    [InlineData("alias", ServiceStartupState.Static)]
    [InlineData("masked", ServiceStartupState.Masked)]
    [InlineData("masked-runtime", ServiceStartupState.Masked)]
    [InlineData("linked", ServiceStartupState.Unknown)]
    public void Parse_maps_systemd_startup_states(string raw, ServiceStartupState expected)
    {
        var service = Assert.Single(SystemdServicesParser.Parse(
            "sample.service loaded active running Sample",
            $"sample.service {raw} -").Services);

        Assert.Equal(expected, service.StartupState);
    }

    [Fact]
    public void Parse_malformed_fixture_keeps_only_valid_service_record()
    {
        var result = SystemdServicesParser.Parse(FixtureText.Read("systemd-malformed.txt"), null);

        var service = Assert.Single(result.Services);
        Assert.Equal("valid.service", service.Id);
        Assert.Equal(ServiceState.Failed, service.State);
    }

    [Fact]
    public void Parse_empty_fixture_is_empty()
    {
        var result = SystemdServicesParser.Parse(FixtureText.Read("systemd-empty.txt"), null);

        Assert.Empty(result.Services);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Parse_large_2000_service_dataset_is_complete_and_bounded()
    {
        var input = string.Join(
            '\n',
            Enumerable.Range(0, 2000)
                .Select(i => $"service-{i:D4}.service loaded active running Representative service {i:D4}"));

        var result = SystemdServicesParser.Parse(input, null);

        Assert.Equal(2000, result.Services.Count);
        Assert.False(result.Truncated);
        Assert.All(result.Services, service =>
        {
            Assert.InRange(service.Id.Length, 1, WorkloadLimits.MaxTextLength);
            Assert.InRange(service.Name.Length, 1, WorkloadLimits.MaxTextLength);
            Assert.InRange(service.DisplayName!.Length, 1, WorkloadLimits.MaxTextLength);
        });
    }

    [Fact]
    public void Parse_hostile_description_is_single_line_escape_free_and_clamped()
    {
        var hostile = "line\tcolumn" + (char)0x1b + "[31mred" + (char)0x1b + "[0m" +
                      "\u202espoof 😀 \"quote\" \\slash " + new string('A', 300);
        var service = Assert.Single(
            SystemdServicesParser.Parse($"evil.service loaded active running {hostile}", null).Services);

        Assert.NotNull(service.DisplayName);
        Assert.Equal(WorkloadLimits.MaxTextLength, service.DisplayName!.Length);
        Assert.DoesNotContain('\t', service.DisplayName);
        Assert.DoesNotContain((char)0x1b, service.DisplayName);
        Assert.DoesNotContain('\u202e', service.DisplayName);
        Assert.Contains("😀", service.DisplayName, StringComparison.Ordinal);
        Assert.Contains("\"quote\" \\slash", service.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_maps_active_substate_to_service_state()
    {
        var services = SystemdServicesParser.Parse(ListUnits, ListUnitFiles).Services;

        Assert.Equal(ServiceState.Running, services.Single(s => s.Id == "ssh.service").State);
        Assert.Equal(ServiceState.Failed, services.Single(s => s.Id == "nginx.service").State);
        Assert.Equal(ServiceState.Stopped, services.Single(s => s.Id == "apt-daily.service").State);
        Assert.Equal(ServiceState.Running, services.Single(s => s.Id == "oneshot.service").State); // active (exited)
    }

    [Fact]
    public void Parse_joins_startup_state_from_unit_files_by_unit_id()
    {
        var services = SystemdServicesParser.Parse(ListUnits, ListUnitFiles).Services;

        Assert.Equal(ServiceStartupState.Enabled, services.Single(s => s.Id == "ssh.service").StartupState);
        Assert.Equal(ServiceStartupState.Disabled, services.Single(s => s.Id == "nginx.service").StartupState);
        Assert.Equal(ServiceStartupState.Static, services.Single(s => s.Id == "apt-daily.service").StartupState);
        Assert.Equal(ServiceStartupState.Masked, services.Single(s => s.Id == "oneshot.service").StartupState);
        // cron.service is absent from unit-files → unknown → null (not fabricated).
        Assert.Null(services.Single(s => s.Id == "cron.service").StartupState);
    }

    [Fact]
    public void Parse_without_unit_files_leaves_startup_state_null()
    {
        var services = SystemdServicesParser.Parse(ListUnits, null).Services;

        Assert.All(services, s => Assert.Null(s.StartupState));
    }

    [Fact]
    public void Parse_ignores_non_service_lines()
    {
        var input = "not-a-unit line here\n" + ListUnits;

        var services = SystemdServicesParser.Parse(input, null).Services;

        Assert.Equal(5, services.Count);
        Assert.All(services, s => Assert.EndsWith(".service", s.Id));
    }

    [Theory]
    [InlineData("activating", ServiceState.Starting)]
    [InlineData("deactivating", ServiceState.Stopping)]
    [InlineData("reloading", ServiceState.Running)]
    [InlineData("weird", ServiceState.Unknown)]
    public void Parse_maps_transient_active_states(string activeState, ServiceState expected)
    {
        var line = $"x.service loaded {activeState} sub Some description";

        var service = Assert.Single(SystemdServicesParser.Parse(line, null).Services);

        Assert.Equal(expected, service.State);
    }

    [Fact]
    public void Parse_caps_the_list_and_flags_truncation()
    {
        var lines = Enumerable.Range(0, WorkloadLimits.MaxServices + 5)
            .Select(i => $"svc{i}.service loaded active running Service number {i}");
        var input = string.Join('\n', lines);

        var result = SystemdServicesParser.Parse(input, null);

        Assert.Equal(WorkloadLimits.MaxServices, result.Services.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Parse_sanitizes_description()
    {
        // A real ESC + CSI color sequence embedded in the description must be stripped.
        var line = "x.service loaded active running desc" + (char)0x1b + "[31mwith-ansi";

        var service = Assert.Single(SystemdServicesParser.Parse(line, null).Services);

        Assert.Equal("descwith-ansi", service.DisplayName);
    }
}
