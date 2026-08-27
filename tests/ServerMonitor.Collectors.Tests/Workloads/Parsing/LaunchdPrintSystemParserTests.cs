using ServerMonitor.Collectors.Workloads.Parsing;
using ServerMonitor.Collectors.Tests.Workloads;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.Collectors.Tests.Workloads.Parsing;

public sealed class LaunchdPrintSystemParserTests
{
    private const string PrintSystem =
        "system = {\n" +
        "\tactive count = 42\n" +
        "\tservices = {\n" +
        "\t\t123    0    com.apple.sshd\n" +
        "\t\t-      -    com.apple.stopped.daemon\n" +
        "\t\t-      78   com.openssh.failed.daemon\n" +
        "\t\t0      0    com.apple.zero.pid\n" +
        "\t}\n" +
        "}\n";

    [Fact]
    public void Parse_maps_running_and_stopped_from_pid_only()
    {
        var services = LaunchdPrintSystemParser.Parse(PrintSystem).Services;

        Assert.Equal(ServiceState.Running, services.Single(s => s.Id == "com.apple.sshd").State);
        Assert.Equal(ServiceState.Stopped, services.Single(s => s.Id == "com.apple.stopped.daemon").State);
        // H-04: a not-running job with a non-zero last-exit token is Stopped, never fabricated Failed.
        Assert.Equal(ServiceState.Stopped, services.Single(s => s.Id == "com.openssh.failed.daemon").State);
        Assert.Equal(ServiceState.Stopped, services.Single(s => s.Id == "com.apple.zero.pid").State);
    }

    [Fact]
    public void Parse_representative_fixture_maps_running_and_stopped()
    {
        var result = LaunchdPrintSystemParser.Parse(FixtureText.Read("launchd-print-system.txt"));

        Assert.Equal(4, result.Services.Count);
        Assert.False(result.Truncated);
        Assert.Equal(ServiceState.Running, result.Services.Single(s => s.Id == "com.apple.sshd").State);
        Assert.Equal(ServiceState.Stopped, result.Services.Single(s => s.Id == "com.example.stopped").State);
        // H-04: not-running + non-zero last exit is Stopped, not Failed (launchd summary can't tell a real
        // failure from a normal one-shot non-zero exit).
        Assert.Equal(ServiceState.Stopped, result.Services.Single(s => s.Id == "com.example.failed").State);
        Assert.Equal(ServiceState.Stopped, result.Services.Single(s => s.Id == "com.example.zero-pid").State);
    }

    [Fact]
    public void Parse_real_macos26_dump_never_over_reports_failed()
    {
        // Ground-truthed against the literal mac-mini dump (macOS 26.6, 428 services). The three legitimate
        // one-shot loaders exit 1 by design and must read Stopped, not Failed (H-04).
        var result = LaunchdPrintSystemParser.Parse(FixtureText.Read("launchd-print-system-macos26.txt"));

        Assert.Equal(428, result.Services.Count);
        Assert.False(result.Truncated);
        Assert.False(result.IsUnrecognized);
        Assert.Equal(173, result.Services.Count(s => s.State == ServiceState.Running));
        Assert.Equal(255, result.Services.Count(s => s.State == ServiceState.Stopped));
        Assert.DoesNotContain(result.Services, s => s.State == ServiceState.Failed);

        foreach (var oneShot in new[]
                 {
                     "com.apple.wifiFirmwareLoader",
                     "com.apple.iomfb_fdr_loader",
                     "com.example.oneshot-check"
                 })
        {
            Assert.Equal(ServiceState.Stopped, result.Services.Single(s => s.Id == oneShot).State);
        }
    }

    [Fact]
    public void Parse_malformed_fixture_keeps_only_valid_row()
    {
        var service = Assert.Single(
            LaunchdPrintSystemParser.Parse(FixtureText.Read("launchd-malformed.txt")).Services);

        Assert.Equal("com.example.valid", service.Id);
        Assert.Equal(ServiceState.Running, service.State);
    }

    [Fact]
    public void Parse_empty_fixture_is_empty()
    {
        var result = LaunchdPrintSystemParser.Parse(FixtureText.Read("launchd-empty.txt"));

        Assert.Empty(result.Services);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Parse_large_2000_service_dataset_is_complete_and_bounded()
    {
        var rows = Enumerable.Range(0, 2000)
            .Select(i => $"\t\t{i + 1}\t0\tcom.example.service-{i:D4}");
        var input = "system = {\n\tservices = {\n" + string.Join('\n', rows) + "\n\t}\n}\n";

        var result = LaunchdPrintSystemParser.Parse(input);

        Assert.Equal(2000, result.Services.Count);
        Assert.False(result.Truncated);
        Assert.All(result.Services, service =>
        {
            Assert.InRange(service.Id.Length, 1, WorkloadLimits.MaxTextLength);
            Assert.InRange(service.Name.Length, 1, WorkloadLimits.MaxTextLength);
        });
    }

    [Fact]
    public void Parse_hostile_label_is_escape_free_clamped_and_keeps_safe_characters()
    {
        var hostile = "com.example." + (char)0x1b + "[31mred" + (char)0x1b + "[0m" +
                      "\u202espoof😀\"quote\"\\slash-" + new string('A', 300);
        var input = $"system = {{\n\tservices = {{\n\t\t123 0 {hostile}\n\t}}\n}}\n";

        var service = Assert.Single(LaunchdPrintSystemParser.Parse(input).Services);

        Assert.Equal(WorkloadLimits.MaxTextLength, service.Id.Length);
        Assert.DoesNotContain((char)0x1b, service.Id);
        Assert.DoesNotContain('\u202e', service.Id);
        Assert.Contains("😀", service.Id, StringComparison.Ordinal);
        Assert.Contains("\"quote\"\\slash", service.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_uses_full_label_as_name_and_leaves_platform_fields_null()
    {
        var service = LaunchdPrintSystemParser.Parse(PrintSystem).Services.Single(s => s.Id == "com.apple.sshd");

        // Name is the full label, never collapsed to a segment (a leading "com" or an ambiguous tail).
        Assert.Equal("com.apple.sshd", service.Name);
        Assert.Equal(service.Id, service.Name);
        Assert.Null(service.DisplayName);
        Assert.Null(service.SubState);
        Assert.Null(service.StartupState);
    }

    [Fact]
    public void Parse_reverse_dns_labels_yield_distinct_meaningful_names()
    {
        // Regression: reverse-DNS labels must not all collapse to "com" (or any shared segment).
        var input =
            "system = {\n\tservices = {\n" +
            "\t\t101 0 com.apple.sshd\n" +
            "\t\t102 0 com.acme.agent\n" +
            "\t\t103 0 org.openssh.sshd\n" +
            "\t}\n}\n";

        var names = LaunchdPrintSystemParser.Parse(input).Services.Select(s => s.Name).ToList();

        Assert.Equal(new[] { "com.apple.sshd", "com.acme.agent", "org.openssh.sshd" }, names);
        Assert.Equal(3, names.Distinct().Count());
        Assert.DoesNotContain("com", names);
    }

    [Fact]
    public void Parse_stops_at_the_end_of_the_services_block()
    {
        // A label-like token after the closing brace must not be picked up.
        var input = PrintSystem + "\textra = {\n\t\t999 0 com.should.not.appear\n\t}\n";

        var services = LaunchdPrintSystemParser.Parse(input).Services;

        Assert.DoesNotContain(services, s => s.Id == "com.should.not.appear");
        Assert.Equal(4, services.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("system = {\n\tno services table here\n}")]
    public void Parse_without_services_block_returns_empty(string? input)
    {
        var result = LaunchdPrintSystemParser.Parse(input);

        Assert.Empty(result.Services);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Parse_caps_the_list_and_flags_truncation()
    {
        var rows = Enumerable.Range(0, WorkloadLimits.MaxServices + 5)
            .Select(i => $"\t\t{i + 1}\t0\tcom.example.svc{i}");
        var input = "system = {\n\tservices = {\n" + string.Join('\n', rows) + "\n\t}\n}\n";

        var result = LaunchdPrintSystemParser.Parse(input);

        Assert.Equal(WorkloadLimits.MaxServices, result.Services.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Parse_sanitizes_label()
    {
        // A real ESC + CSI color sequence embedded in the label must be stripped.
        var input = "system = {\n\tservices = {\n\t\t123 0 com.evil" + (char)0x1b + "[31mlabel\n\t}\n}\n";

        var service = Assert.Single(LaunchdPrintSystemParser.Parse(input).Services);

        Assert.Equal("com.evillabel", service.Id);
    }
}
