using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Probes;

namespace ServerMonitor.Core.Tests.Probes;

public sealed class RemoteReadRegistryTests
{
    private const string Script = "echo \"@@read\"\njournalctl -u \"$1\" -n 50 --no-pager -q 2>/dev/null </dev/null";

    [Fact]
    public void A_valid_read_is_accepted_with_the_values_it_had_at_start()
    {
        var read = new FakeRead("unit.log");

        var registry = new RemoteReadRegistry([read]);
        read.Command = "echo changed \"$1\"";

        Assert.Equal(1, registry.Count);
        Assert.Empty(registry.Rejected);
        Assert.True(registry.TryGet("unit.log", out var registration));
        Assert.Equal(Script, registration.Command);
        Assert.Equal(ServerOperatingSystem.Linux, registration.OperatingSystem);
        Assert.Equal(RemoteReadArgumentKind.SystemdUnit, registration.ArgumentKind);
        Assert.Equal(64 * 1024, registration.OutputLimitBytes);
        Assert.Equal(TimeSpan.FromSeconds(10), registration.Timeout);
        Assert.False(registry.TryGet("UNIT.LOG", out _));
        Assert.False(registry.TryGet(null, out _));
    }

    [Fact]
    public void Invalid_reads_are_left_out_with_reasons_that_never_quote_the_script()
    {
        var reads = new IRemoteRead?[]
        {
            new FakeRead("ok"),
            new FakeRead("no-arg") { Command = "echo secret-two" },
            new FakeRead("unquoted") { Command = "echo $1 secret-two" },
            new FakeRead("all") { Command = "echo \"$1\" \"$@\" secret-two" },
            new FakeRead("star") { Command = "echo \"$1\" $* secret-two" },
            new FakeRead("two") { Command = "echo \"$1\" \"$2\" secret-two" },
            new FakeRead("nine") { Command = "echo \"$1\" $9 secret-two" },
            new FakeRead("brace2") { Command = "echo \"$1\" \"${2}\" secret-two" },
            new FakeRead("brace10") { Command = "echo \"$1\" \"${10}\" secret-two" },
            new FakeRead("brace-all") { Command = "echo \"$1\" \"${@}\" secret-two" },
            new FakeRead("shift") { Command = "shift\necho \"$1\" secret-two" },
            new FakeRead("set") { Command = "set -- x\necho \"$1\" secret-two" },
            new FakeRead("set-e") { Command = "set -e; echo \"$1\" secret-two" },
            new FakeRead("eval") { Command = "eval echo \"$1\" secret-two" },
            new FakeRead("cr") { Command = "echo \"$1\"\rsecret-two" },
            new FakeRead("crlf") { Command = "echo \"$1\"\r\nsecret-two" },
            new FakeRead("long") { Command = "echo \"$1\" " + new string('x', RemoteProbeLimits.MaxCommandLength) },
            new FakeRead("cap0") { OutputLimitBytes = 0 },
            new FakeRead("capbig") { OutputLimitBytes = RemoteProbeLimits.MaxOutputLimitBytes + 1 },
            new FakeRead("t0") { Timeout = TimeSpan.Zero },
            new FakeRead("tbig") { Timeout = RemoteProbeLimits.MaxTimeout + TimeSpan.FromSeconds(1) },
            new FakeRead("Upper"),
            new FakeRead("mac-unit") { OperatingSystem = ServerOperatingSystem.MacOS },
            new FakeRead("auto-unit") { OperatingSystem = ServerOperatingSystem.Auto },
            new FakeRead("kind") { ArgumentKind = (RemoteReadArgumentKind)42 },
            new FakeRead("ok"),
            new FakeRead("probe.id"),
            new ThrowingRead(),
            null,
        };

        var registry = new RemoteReadRegistry(reads!, ["probe.id"]);

        Assert.Equal(1, registry.Count);
        Assert.Equal(reads.Length - 1, registry.Rejected.Count);
        Assert.DoesNotContain(registry.Rejected, reason => reason.Contains("secret-two", StringComparison.Ordinal));
        Assert.DoesNotContain(registry.Rejected, reason => reason.Contains("journalctl", StringComparison.Ordinal));
        Assert.All(registry.Rejected, reason => Assert.StartsWith("read #", reason, StringComparison.Ordinal));
        Assert.Contains("read #1: its script never uses \"$1\"", registry.Rejected);
        Assert.Contains("read #2: its script never uses \"$1\"", registry.Rejected);
        Assert.Contains("read #3: its script uses a positional parameter other than $1", registry.Rejected);
        Assert.Contains("read #10: its script uses shift, set or eval", registry.Rejected);
        Assert.Contains("read #22: a systemd unit read must run on Linux", registry.Rejected);
        Assert.Contains("read #24: its argument kind is not known", registry.Rejected);
        Assert.Contains("read #25: another read already has its id", registry.Rejected);
        Assert.Contains("read #26: a probe already has its id", registry.Rejected);
        Assert.Contains("read #27: reading it threw InvalidOperationException", registry.Rejected);
        Assert.Contains("read #28: it is null", registry.Rejected);
    }

    [Theory]
    [InlineData("echo \"$1\" offset=1")]
    [InlineData("echo \"$1\" reset_x evaluate shifted")]
    [InlineData("echo \"$1\" \"$10\"")]
    [InlineData("echo \"$1\" \"$#\" \"$?\" \"$$\"")]
    public void Words_and_parameters_that_only_look_alike_are_accepted(string command)
    {
        // "$10" is "$1" followed by "0" in POSIX sh, so it is still the one argument.
        var registry = new RemoteReadRegistry([new FakeRead("ok") { Command = command }]);

        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void A_container_read_may_run_on_macos()
    {
        var registry = new RemoteReadRegistry(
        [
            new FakeRead("ctr") { OperatingSystem = ServerOperatingSystem.MacOS, ArgumentKind = RemoteReadArgumentKind.ContainerId },
        ]);

        Assert.True(registry.TryGet("ctr", out var registration));
        Assert.Equal(ServerOperatingSystem.MacOS, registration.OperatingSystem);
    }

    [Fact]
    public void A_registration_never_prints_its_script()
    {
        var registry = new RemoteReadRegistry([new FakeRead("unit.log")]);
        Assert.True(registry.TryGet("unit.log", out var registration));

        Assert.DoesNotContain("journalctl", registration.ToString(), StringComparison.Ordinal);
        Assert.Contains("unit.log", registration.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_scheduler_lists_its_probe_ids_for_the_registry()
    {
        var scheduler = new RemoteProbeScheduler(
            [new FakeProbe("probe.a"), new FakeProbe("probe.b")],
            new HistoryGeneration());

        var registry = new RemoteReadRegistry([new FakeRead("probe.a"), new FakeRead("read.c")], scheduler.ProbeIds);

        Assert.Equal(["probe.a", "probe.b"], scheduler.ProbeIds.Order(StringComparer.Ordinal));
        Assert.Equal(1, registry.Count);
        Assert.Equal("read #0: a probe already has its id", Assert.Single(registry.Rejected));
    }

    private sealed class FakeRead(string id) : IRemoteRead
    {
        public string Id { get; } = id;

        public ServerOperatingSystem OperatingSystem { get; init; } = ServerOperatingSystem.Linux;

        public string Command { get; set; } = Script;

        public RemoteReadArgumentKind ArgumentKind { get; init; } = RemoteReadArgumentKind.SystemdUnit;

        public int OutputLimitBytes { get; init; } = 64 * 1024;

        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    }

    private sealed class ThrowingRead : IRemoteRead
    {
        public string Id => "throws";

        public ServerOperatingSystem OperatingSystem => ServerOperatingSystem.Linux;

        public string Command => throw new InvalidOperationException("secret-two");

        public RemoteReadArgumentKind ArgumentKind => RemoteReadArgumentKind.SystemdUnit;

        public int OutputLimitBytes => 1024;

        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
    }

    private sealed class FakeProbe(string id) : IRemoteProbe
    {
        public string Id { get; } = id;

        public ServerOperatingSystem OperatingSystem => ServerOperatingSystem.Linux;

        public string Command => "uptime";

        public int OutputLimitBytes => 1024;

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public TimeSpan MinimumInterval => TimeSpan.Zero;

        public void Accept(RemoteProbeOutcome outcome)
        {
        }
    }
}

public sealed class RemoteReadArgumentTests
{
    [Theory]
    [InlineData("ssh.service")]
    [InlineData("getty@tty1.service")]
    [InlineData("user@1000.service")]
    [InlineData("a:b.service")]
    [InlineData("_x.service")]
    [InlineData("@x.service")]
    [InlineData("dbus-org.freedesktop.network1.service")]
    public void Unit_ids_systemd_uses_are_valid(string unit) =>
        Assert.True(RemoteReadArgument.IsValid(RemoteReadArgumentKind.SystemdUnit, unit));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".service")]
    [InlineData("nginx")]
    [InlineData("nginx.socket")]
    [InlineData("nginx.SERVICE")]
    [InlineData("-f.service")]
    [InlineData(".x.service")]
    [InlineData("a b.service")]
    [InlineData("a'b.service")]
    [InlineData("a\"b.service")]
    [InlineData("a\\b.service")]
    [InlineData("a$b.service")]
    [InlineData("a;b.service")]
    [InlineData("a/b.service")]
    [InlineData("a\nb.service")]
    [InlineData("nginx.service\n")]
    [InlineData("nginx.service\r\n")]
    [InlineData("system\\x2dgetty.service")]
    [InlineData("aé.service")]
    [InlineData("a​.service")]
    public void Anything_else_is_not_a_unit(string? unit) =>
        Assert.False(RemoteReadArgument.IsValid(RemoteReadArgumentKind.SystemdUnit, unit));

    [Fact]
    public void A_unit_may_be_200_characters_and_no_more()
    {
        var max = new string('a', RemoteReadArgument.MaxSystemdUnitLength - ".service".Length) + ".service";

        Assert.True(RemoteReadArgument.IsValid(RemoteReadArgumentKind.SystemdUnit, max));
        Assert.False(RemoteReadArgument.IsValid(RemoteReadArgumentKind.SystemdUnit, "a" + max));
    }

    [Fact]
    public void A_container_id_is_exactly_64_lower_case_hex()
    {
        Assert.True(RemoteReadArgument.IsValid(RemoteReadArgumentKind.ContainerId, new string('0', 64)));
        Assert.True(RemoteReadArgument.IsValid(RemoteReadArgumentKind.ContainerId, string.Concat(Enumerable.Repeat("0123456789abcdef", 4))));
        Assert.False(RemoteReadArgument.IsValid(RemoteReadArgumentKind.ContainerId, new string('a', 63)));
        Assert.False(RemoteReadArgument.IsValid(RemoteReadArgumentKind.ContainerId, new string('a', 65)));
        Assert.False(RemoteReadArgument.IsValid(RemoteReadArgumentKind.ContainerId, new string('A', 64)));
        Assert.False(RemoteReadArgument.IsValid(RemoteReadArgumentKind.ContainerId, new string('g', 64)));
        Assert.False(RemoteReadArgument.IsValid(RemoteReadArgumentKind.ContainerId, "0123456789ab"));
        Assert.False(RemoteReadArgument.IsValid(RemoteReadArgumentKind.ContainerId, null));
        Assert.False(RemoteReadArgument.IsValid((RemoteReadArgumentKind)42, new string('0', 64)));
    }
}
