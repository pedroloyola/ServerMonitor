using System.Diagnostics;
using System.Text;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Probes;
using ServerMonitor.Core.Workloads;
using ServerMonitor.Infrastructure.SSH;
using Xunit.Sdk;

namespace ServerMonitor.Infrastructure.Tests.SSH;

/// <summary>
/// Registered one-argument reads: the argument reaches the server only as the single-quoted word of one fixed first line
/// on the standard input of the constant <c>sh -s</c>, after every check passed, and never in a log.
/// </summary>
public sealed class RemoteReadRunnerTests
{
    private const string UnitScript =
        "echo \"@@unit\"\n{ LC_ALL=C journalctl -u \"$1\" -n 50 --no-pager -q 2>/dev/null </dev/null; printf '\\n@@rc=%s\\n' \"$?\"; }";

    private const string ContainerScript =
        "echo \"@@ctr\"\n{ docker logs --tail 50 --timestamps \"$1\" 2>&1 </dev/null; printf '\\n@@rc=%s\\n' \"$?\"; }";

    private static readonly Guid LinuxServer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherLinuxServer = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid MacServer = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AutoServer = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid NoCredentialServer = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid UnknownOsServer = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static readonly string LongUnit = new string('a', RemoteReadArgument.MaxSystemdUnitLength - ".service".Length) + ".service";

    private static readonly string[] UnitCorpus =
    [
        "ssh.service",
        "nginx.service",
        "getty@tty1.service",
        "user@1000.service",
        "systemd-journald.service",
        "dbus-org.freedesktop.network1.service",
        "a:b.service",
        "_x.service",
        "A-Z_0.9.service",
        LongUnit,
    ];

    private static readonly string[] ContainerCorpus =
    [
        new string('0', 64),
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "f1e2d3c4b5a69788f1e2d3c4b5a69788f1e2d3c4b5a69788f1e2d3c4b5a69788",
    ];

    public static TheoryData<string> ValidUnits => new(UnitCorpus);

    public static TheoryData<string> ValidContainers => new(ContainerCorpus);

    public static readonly TheoryData<string> InvalidUnits = new()
    {
        "-f",
        "--follow",
        "-u.service",
        ".hidden.service",
        "a;b.service",
        "$(id).service",
        "`id`.service",
        "a'b.service",
        "a\"b.service",
        "a b.service",
        "a\tb.service",
        "nginx.service\n",
        "nginx\u00e9.service",
        "ngi\u200bnx.service",
        new string('a', RemoteReadArgument.MaxSystemdUnitLength + 1 - ".service".Length) + ".service",
        "nginx",
        ".service",
        "",
    };

    public static readonly TheoryData<string> InvalidContainers = new()
    {
        new string('a', 63),
        new string('a', 65),
        new string('A', 64),
        "0123456789AB",
        "0123456789ab",
        new string('a', 63) + "\n",
        new string('a', 63) + "'",
        "",
    };

    // ---- 1. The argument never enters the command text. ----

    [Theory]
    [MemberData(nameof(ValidUnits))]
    public async Task A_unit_reaches_the_server_only_in_the_first_line_of_standard_input(string unit)
    {
        var fixture = new Fixture();

        var outcome = await fixture.Runner.RunAsync("unit.log", LinuxServer, unit, CancellationToken.None);

        Assert.Equal(RemoteReadOutcomeKind.Completed, outcome.Kind);
        var call = Assert.Single(fixture.Transport.Calls);
        Assert.Equal(LinuxServer, call.Server.Id);
        AssertSafeExec(call.Exec, UnitScript, unit);
    }

    [Theory]
    [MemberData(nameof(ValidContainers))]
    public async Task A_container_reaches_the_server_only_in_the_first_line_of_standard_input(string container)
    {
        var fixture = new Fixture();

        var outcome = await fixture.Runner.RunAsync("container.log", LinuxServer, container, CancellationToken.None);

        Assert.Equal(RemoteReadOutcomeKind.Completed, outcome.Kind);
        AssertSafeExec(Assert.Single(fixture.Transport.Calls).Exec, ContainerScript, container);
    }

    [Fact]
    public async Task The_exec_carries_the_reads_own_limit_and_timeout()
    {
        var fixture = new Fixture();

        await fixture.Runner.RunAsync("unit.log", LinuxServer, "ssh.service", CancellationToken.None);

        var call = Assert.Single(fixture.Transport.Calls);
        Assert.Equal(128 * 1024, call.Exec.OutputLimitBytes);
        Assert.Equal(TimeSpan.FromSeconds(10), call.Exec.Timeout);
        Assert.Equal(RemoteReadRunner.ConnectionTimeout, call.ConnectionTimeout);
    }

    // ---- 2. That test bites. ----

    [Fact]
    public async Task The_check_fails_for_an_exec_that_puts_the_argument_on_the_command_line()
    {
        var fixture = new Fixture((read, argument) => new RemoteReadExec(
            "sh -s -- '" + argument + "'",
            ProbeShellInvocation.StandardInput(read.Command),
            read.OutputLimitBytes,
            read.Timeout));

        await fixture.Runner.RunAsync("unit.log", LinuxServer, "ssh.service", CancellationToken.None);

        var exec = Assert.Single(fixture.Transport.Calls).Exec;
        Assert.ThrowsAny<XunitException>(() => AssertSafeExec(exec, UnitScript, "ssh.service"));
    }

    [Fact]
    public async Task The_check_fails_for_an_exec_that_writes_the_argument_into_the_script()
    {
        var fixture = new Fixture((read, argument) => new RemoteReadExec(
            ProbeShellInvocation.CommandText,
            ProbeShellInvocation.StandardInput(read.Command.Replace("\"$1\"", "'" + argument + "'", StringComparison.Ordinal)),
            read.OutputLimitBytes,
            read.Timeout));

        await fixture.Runner.RunAsync("unit.log", LinuxServer, "ssh.service", CancellationToken.None);

        var exec = Assert.Single(fixture.Transport.Calls).Exec;
        Assert.ThrowsAny<XunitException>(() => AssertSafeExec(exec, UnitScript, "ssh.service"));
    }

    // ---- 3. Refused before any SSH work. ----

    [Theory]
    [MemberData(nameof(InvalidUnits))]
    public async Task A_malformed_unit_is_refused_before_any_ssh_work(string unit)
    {
        var fixture = new Fixture();

        var outcome = await fixture.Runner.RunAsync("unit.log", LinuxServer, unit, CancellationToken.None);

        AssertRefused(fixture, outcome, RemoteReadRefusal.InvalidArgument);
    }

    [Theory]
    [MemberData(nameof(InvalidContainers))]
    public async Task A_malformed_container_id_is_refused_before_any_ssh_work(string container)
    {
        var fixture = new Fixture();

        var outcome = await fixture.Runner.RunAsync("container.log", LinuxServer, container, CancellationToken.None);

        AssertRefused(fixture, outcome, RemoteReadRefusal.InvalidArgument);
    }

    [Fact]
    public async Task A_null_argument_is_refused_before_any_ssh_work()
    {
        var fixture = new Fixture();

        var outcome = await fixture.Runner.RunAsync("unit.log", LinuxServer, null!, CancellationToken.None);

        AssertRefused(fixture, outcome, RemoteReadRefusal.InvalidArgument);
    }

    [Fact]
    public async Task A_valid_unit_that_is_not_listed_is_refused()
    {
        var fixture = new Fixture();

        var outcome = await fixture.Runner.RunAsync("unit.log", LinuxServer, "absent.service", CancellationToken.None);

        AssertRefused(fixture, outcome, RemoteReadRefusal.NotListed);
    }

    [Fact]
    public async Task A_unit_listed_only_on_another_server_is_refused()
    {
        var fixture = new Fixture();

        var outcome = await fixture.Runner.RunAsync("unit.log", LinuxServer, "elsewhere.service", CancellationToken.None);

        AssertRefused(fixture, outcome, RemoteReadRefusal.NotListed);
    }

    [Fact]
    public async Task A_container_listed_only_by_its_short_id_is_refused()
    {
        var fixture = new Fixture();

        // The 12-hex prefix of a listed container whose full id was not carried: the full id is not in the list.
        var outcome = await fixture.Runner.RunAsync("container.log", LinuxServer, new string('9', 64), CancellationToken.None);

        AssertRefused(fixture, outcome, RemoteReadRefusal.NotListed);
    }

    [Fact]
    public async Task A_container_listed_only_on_another_server_is_refused()
    {
        var fixture = new Fixture();

        var outcome = await fixture.Runner.RunAsync("container.log", LinuxServer, new string('e', 64), CancellationToken.None);

        AssertRefused(fixture, outcome, RemoteReadRefusal.NotListed);
    }

    [Fact]
    public async Task A_unit_listed_by_a_service_manager_other_than_systemd_is_refused()
    {
        var fixture = new Fixture();
        fixture.Workloads.Set(Snapshot(LinuxServer, ServiceManager.Launchd, ["ssh.service"], []));

        var outcome = await fixture.Runner.RunAsync("unit.log", LinuxServer, "ssh.service", CancellationToken.None);

        AssertRefused(fixture, outcome, RemoteReadRefusal.NotListed);
    }

    [Fact]
    public async Task A_server_with_no_list_yet_refuses_every_argument()
    {
        var fixture = new Fixture();
        fixture.Workloads.Remove(LinuxServer);

        var outcome = await fixture.Runner.RunAsync("container.log", LinuxServer, ValidContainerId, CancellationToken.None);

        AssertRefused(fixture, outcome, RemoteReadRefusal.NotListed);
    }

    [Fact]
    public async Task A_unit_read_on_a_mac_is_refused_for_its_operating_system()
    {
        var fixture = new Fixture();

        var outcome = await fixture.Runner.RunAsync("unit.log", MacServer, "ssh.service", CancellationToken.None);

        AssertRefused(fixture, outcome, RemoteReadRefusal.WrongOperatingSystem);
    }

    [Fact]
    public async Task An_auto_server_takes_its_operating_system_from_its_latest_service_list()
    {
        var fixture = new Fixture();

        var linux = await fixture.Runner.RunAsync("unit.log", AutoServer, "ssh.service", CancellationToken.None);
        fixture.Workloads.Set(Snapshot(AutoServer, ServiceManager.Launchd, [], []));
        var mac = await fixture.Runner.RunAsync("unit.log", AutoServer, "ssh.service", CancellationToken.None);
        fixture.Workloads.Remove(AutoServer);
        var unknown = await fixture.Runner.RunAsync("unit.log", AutoServer, "ssh.service", CancellationToken.None);

        Assert.Equal(RemoteReadOutcomeKind.Completed, linux.Kind);
        Assert.Equal(RemoteReadRefusal.WrongOperatingSystem, mac.Refusal);
        Assert.Equal(RemoteReadRefusal.WrongOperatingSystem, unknown.Refusal);
        Assert.Single(fixture.Transport.Calls);
    }

    [Fact]
    public async Task An_unknown_read_id_is_refused_first()
    {
        var fixture = new Fixture();

        Assert.Equal(RemoteReadRefusal.UnknownRead, (await fixture.Runner.RunAsync("nope", LinuxServer, "-f", CancellationToken.None)).Refusal);
        Assert.Equal(RemoteReadRefusal.UnknownRead, (await fixture.Runner.RunAsync(null!, Guid.NewGuid(), "-f", CancellationToken.None)).Refusal);
        Assert.Equal(RemoteReadRefusal.UnknownRead, (await fixture.Runner.RunAsync("UNIT.LOG", LinuxServer, "ssh.service", CancellationToken.None)).Refusal);
        Assert.Empty(fixture.Transport.Calls);
    }

    [Theory]
    [InlineData("77777777-7777-7777-7777-777777777777")]
    [InlineData("55555555-5555-5555-5555-555555555555")]
    [InlineData("66666666-6666-6666-6666-666666666666")]
    public async Task A_missing_unmonitored_or_credential_less_server_is_unavailable(string serverId)
    {
        var fixture = new Fixture();

        var outcome = await fixture.Runner.RunAsync("unit.log", Guid.Parse(serverId), "-f", CancellationToken.None);

        AssertRefused(fixture, outcome, RemoteReadRefusal.ServerUnavailable);
    }

    [Fact]
    public async Task A_second_read_on_the_same_server_while_one_runs_is_busy_and_another_server_is_not()
    {
        var fixture = new Fixture();
        var release = new TaskCompletionSource();
        fixture.Transport.Hold = release.Task;

        var first = fixture.Runner.RunAsync("unit.log", LinuxServer, "ssh.service", CancellationToken.None);
        await fixture.Transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await fixture.Runner.RunAsync("container.log", LinuxServer, ValidContainerId, CancellationToken.None);
        fixture.Transport.Hold = null;
        var other = await fixture.Runner.RunAsync("unit.log", OtherLinuxServer, "elsewhere.service", CancellationToken.None);
        release.SetResult();
        var firstOutcome = await first;
        var after = await fixture.Runner.RunAsync("unit.log", LinuxServer, "ssh.service", CancellationToken.None);

        Assert.Equal(RemoteReadRefusal.Busy, second.Refusal);
        Assert.Equal(RemoteReadOutcomeKind.Completed, other.Kind);
        Assert.Equal(RemoteReadOutcomeKind.Completed, firstOutcome.Kind);
        Assert.Equal(RemoteReadOutcomeKind.Completed, after.Kind);
        Assert.Equal(3, fixture.Transport.Calls.Count);
    }

    [Fact]
    public async Task The_server_is_free_again_after_a_transport_that_throws()
    {
        var fixture = new Fixture();
        fixture.Transport.Throw = new InvalidOperationException("boom");

        var failed = await fixture.Runner.RunAsync("unit.log", LinuxServer, "ssh.service", CancellationToken.None);
        fixture.Transport.Throw = null;
        var next = await fixture.Runner.RunAsync("unit.log", LinuxServer, "ssh.service", CancellationToken.None);

        Assert.Equal(RemoteReadOutcomeKind.Failed, failed.Kind);
        Assert.Equal(RemoteReadOutcomeKind.Completed, next.Kind);
    }

    [Fact]
    public async Task A_cancelled_caller_gets_a_cancellation_and_frees_the_server()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Transport.Hold = Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);

        var running = fixture.Runner.RunAsync("unit.log", LinuxServer, "ssh.service", cancellation.Token);
        await fixture.Transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        fixture.Transport.Hold = null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(RemoteReadOutcomeKind.Completed, (await fixture.Runner.RunAsync("unit.log", LinuxServer, "ssh.service", CancellationToken.None)).Kind);
    }

    // ---- How an outcome is classified. ----

    [Fact]
    public async Task Outcomes_follow_what_the_session_reported()
    {
        var fixture = new Fixture();

        async Task<RemoteReadOutcome> Run(RemoteReadTransportResult result)
        {
            fixture.Transport.Result = result;
            return await fixture.Runner.RunAsync("unit.log", LinuxServer, "ssh.service", CancellationToken.None);
        }

        var completed = await Run(new(SshConnectionErrorCode.None, new RemoteReadExecution(true, 3, "out", false, false)));
        var exceeded = await Run(new(SshConnectionErrorCode.None, new RemoteReadExecution(true, 0, null, true, false)));
        var tooBig = await Run(new(SshConnectionErrorCode.None, new RemoteReadExecution(true, 0, new string('x', 128 * 1024 + 1), false, false)));
        var timedOut = await Run(new(SshConnectionErrorCode.None, new RemoteReadExecution(false, null, null, false, true)));
        var notUtf8 = await Run(new(SshConnectionErrorCode.None, new RemoteReadExecution(true, 0, null, false, false)));
        var unreachable = await Run(new(SshConnectionErrorCode.ConnectionRefused, null));
        var hostKey = await Run(new(SshConnectionErrorCode.HostKeyMismatch, null));

        Assert.Equal(new RemoteReadOutcome(RemoteReadOutcomeKind.Completed, null, 3, "out"), completed);
        Assert.Equal(new RemoteReadOutcome(RemoteReadOutcomeKind.OutputExceededLimit, null, 0, null), exceeded);
        Assert.Equal(new RemoteReadOutcome(RemoteReadOutcomeKind.OutputExceededLimit, null, 0, null), tooBig);
        Assert.Equal(RemoteReadOutcomeKind.TimedOut, timedOut.Kind);
        Assert.Equal(RemoteReadOutcomeKind.Failed, notUtf8.Kind);
        Assert.Equal(RemoteReadOutcomeKind.Failed, unreachable.Kind);
        Assert.Equal(RemoteReadOutcomeKind.Failed, hostKey.Kind);
    }

    // ---- 4. A real shell agrees. ----

    [Fact]
    public async Task A_real_sh_sees_exactly_one_argument_equal_to_the_one_given()
    {
        var sh = FindSh();
        if (sh is null)
        {
            Assert.True(Environment.GetEnvironmentVariable("CI") is null, "No POSIX sh was found on this CI runner.");
            return;
        }

        const string script = "printf '%s|%s\\n' \"$#\" \"$1\"";
        var arguments = UnitCorpus.Select(unit => (Kind: RemoteReadArgumentKind.SystemdUnit, Argument: unit))
            .Concat(ContainerCorpus.Select(id => (Kind: RemoteReadArgumentKind.ContainerId, Argument: id)));
        foreach (var (kind, argument) in arguments)
        {
            var output = await RunShAsync(sh, RemoteReadInvocation.StandardInput(kind, argument, script));

            Assert.Equal("1|" + argument + "\n", output);
        }
    }

    // ---- 5. Privacy. ----

    private const string CanaryUnit = "canary-7f3a9c21.service";
    private const string CanaryOutput = "hunter2-CANARY-token=sk_live_0000";

    [Fact]
    public async Task The_argument_and_the_output_never_reach_trace_console_exceptions_or_to_string()
    {
        var fixture = new Fixture(unitList: ["ssh.service", CanaryUnit]);
        fixture.Transport.Result = new(SshConnectionErrorCode.None, new RemoteReadExecution(true, 0, CanaryOutput, false, false));

        var captured = await CaptureAsync(async () =>
        {
            var outcomes = new List<object>
            {
                await fixture.Runner.RunAsync("unit.log", LinuxServer, CanaryUnit, CancellationToken.None),
                await fixture.Runner.RunAsync("unit.log", LinuxServer, CanaryUnit + "x", CancellationToken.None),
            };
            fixture.Transport.Throw = new InvalidOperationException("transport failed");
            outcomes.Add(await fixture.Runner.RunAsync("unit.log", LinuxServer, CanaryUnit, CancellationToken.None));
            fixture.Transport.Throw = null;
            outcomes.Add(Assert.Single(fixture.Transport.Calls.Take(1)).Exec);
            outcomes.Add(new RemoteReadExecution(true, 0, CanaryOutput, false, false));
            foreach (var outcome in outcomes)
            {
                Trace.WriteLine(outcome);
                Console.WriteLine(outcome);
            }

            var invalid = Assert.Throws<ArgumentException>(() =>
                RemoteReadInvocation.StandardInput(RemoteReadArgumentKind.SystemdUnit, "canary-7f3a9c21'.service", "echo \"$1\""));
            Trace.WriteLine(invalid.ToString());
            return outcomes;
        });

        Assert.Equal(CanaryOutput, ((RemoteReadOutcome)captured.Result[0]).StandardOutput);
        Assert.DoesNotContain("canary-7f3a9c21", captured.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryOutput, captured.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_capture_sees_a_leak_the_mutant()
    {
        var captured = await CaptureAsync(() =>
        {
            Trace.WriteLine(new LeakyOutcome(CanaryOutput));
            Console.Error.WriteLine(CanaryUnit);
            return Task.FromResult(0);
        });

        Assert.Contains(CanaryOutput, captured.Text, StringComparison.Ordinal);
        Assert.Contains(CanaryUnit, captured.Text, StringComparison.Ordinal);
        Assert.Contains(CanaryOutput, new LeakyOutcome(CanaryOutput).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(CanaryOutput, new RemoteReadOutcome(RemoteReadOutcomeKind.Completed, null, 0, CanaryOutput).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_refuses_any_command_text_but_the_constant_and_keeps_no_standard_error()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ServerMonitor.Infrastructure", "SSH", "SshNetSession.cs"));
        var start = source.IndexOf("private async Task<RemoteReadExecution> ExecuteReadAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task<RemoteCommandOutcome> ExecuteDetailedAsync(", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var method = source[start..end];

        Assert.Contains("string.Equals(read.CommandText, ProbeShellInvocation.CommandText, StringComparison.Ordinal)", method, StringComparison.Ordinal);
        Assert.Contains("throw new InvalidOperationException(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("StandardError", method, StringComparison.Ordinal);
        Assert.DoesNotContain("_logger", method, StringComparison.Ordinal);
    }

    private sealed record LeakyOutcome(string StandardOutput);

    // ---- Helpers. ----

    private const string ValidContainerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>The safety check of test 1. Throws an xUnit failure for any exec that breaks it.</summary>
    private static void AssertSafeExec(RemoteReadExec exec, string script, string argument)
    {
        Assert.Equal("sh -s", exec.CommandText);
        Assert.Equal(ProbeShellInvocation.CommandText, exec.CommandText);

        var stdin = Encoding.UTF8.GetString(exec.StandardInput);
        Assert.Equal("set -- '" + argument + "'\n" + script + "\n", stdin);

        var firstLineEnd = Array.IndexOf(exec.StandardInput, (byte)'\n');
        Assert.True(firstLineEnd > 0);
        Assert.Equal(Encoding.UTF8.GetBytes(script + "\n"), exec.StandardInput[(firstLineEnd + 1)..]);

        var first = stdin.IndexOf(argument, StringComparison.Ordinal);
        Assert.True(first >= 0 && first < firstLineEnd);
        Assert.Equal(-1, stdin.IndexOf(argument, first + 1, StringComparison.Ordinal));
    }

    private static void AssertRefused(Fixture fixture, RemoteReadOutcome outcome, RemoteReadRefusal refusal)
    {
        Assert.Equal(RemoteReadOutcome.Refused(refusal), outcome);
        Assert.Empty(fixture.Transport.Calls);
        Assert.Equal(0, fixture.BuildCount);
    }

    private static string? FindSh()
    {
        if (!OperatingSystem.IsWindows())
        {
            return File.Exists("/bin/sh") ? "/bin/sh" : null;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new[]
            {
                Path.Combine(programFiles, "Git", "bin", "sh.exe"),
                Path.Combine(programFiles, "Git", "usr", "bin", "sh.exe"),
            }
            .FirstOrDefault(File.Exists);
    }

    private static async Task<string> RunShAsync(string sh, byte[] standardInput)
    {
        var start = new ProcessStartInfo(sh, "-s")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        await process.StandardInput.BaseStream.WriteAsync(standardInput);
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        return output.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Runs <paramref name="action"/> with Trace (Debug shares its listeners) and the console captured.</summary>
    private static async Task<(string Text, T Result)> CaptureAsync<T>(Func<Task<T>> action)
    {
        var trace = new StringWriter();
        var console = new StringWriter();
        var listener = new TextWriterTraceListener(trace);
        var (outWriter, errorWriter) = (Console.Out, Console.Error);
        Trace.Listeners.Add(listener);
        Console.SetOut(console);
        Console.SetError(console);
        try
        {
            var result = await action();
            Debug.WriteLine(result);
            Trace.Flush();
            return (trace + "\n" + console, result);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
        }
    }

    private static ServerWorkloadSnapshot Snapshot(
        Guid serverId,
        ServiceManager manager,
        IReadOnlyList<string> units,
        IReadOnlyList<ContainerInfo> containers) => new()
    {
        ServerId = serverId,
        CapturedAtUtc = DateTimeOffset.UnixEpoch,
        Docker = new DockerSnapshot { Availability = DockerAvailability.Available, Containers = containers },
        Services = new ServiceSnapshot
        {
            Manager = manager,
            Availability = WorkloadServiceAvailability.Available,
            Services = [.. units.Select(unit => new ServiceInfo { Id = unit, Name = unit, State = ServiceState.Running })],
        },
    };

    private static ContainerInfo Container(string shortId, string? fullId) => new()
    {
        ContainerId = shortId,
        FullContainerId = fullId,
        Name = "web",
        Image = "nginx:latest",
        State = ContainerState.Running,
        StatusText = "Up",
        Health = ContainerHealth.None,
    };

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate ServerMonitor.slnx from test output.");
    }

    private sealed class Fixture
    {
        public Fixture(
            Func<RemoteReadRegistration, string, RemoteReadExec>? buildExec = null,
            IReadOnlyList<string>? unitList = null)
        {
            var linuxUnits = unitList ?? UnitCorpus;
            Workloads.Set(Snapshot(LinuxServer, ServiceManager.Systemd, linuxUnits,
            [
                .. ContainerCorpus.Select(id => Container(id[..12], id)),
                Container("999999999999", null),
            ]));
            Workloads.Set(Snapshot(OtherLinuxServer, ServiceManager.Systemd, ["elsewhere.service"],
                [Container("eeeeeeeeeeee", new string('e', 64))]));
            Workloads.Set(Snapshot(MacServer, ServiceManager.Launchd, ["ssh.service"], []));
            Workloads.Set(Snapshot(AutoServer, ServiceManager.Systemd, ["ssh.service"], []));
            Workloads.Set(Snapshot(NoCredentialServer, ServiceManager.Systemd, ["ssh.service"], []));
            Workloads.Set(Snapshot(UnknownOsServer, ServiceManager.Systemd, ["ssh.service"], []));

            var registry = new RemoteReadRegistry(
            [
                new Read("unit.log", ServerOperatingSystem.Linux, UnitScript, RemoteReadArgumentKind.SystemdUnit),
                new Read("container.log", ServerOperatingSystem.Linux, ContainerScript, RemoteReadArgumentKind.ContainerId),
            ]);
            Assert.Equal(2, registry.Count);

            var build = buildExec ?? RemoteReadInvocation.Build;
            Runner = new RemoteReadRunner(registry, Servers, Workloads, Transport, (read, argument) =>
            {
                BuildCount++;
                return build(read, argument);
            });
        }

        public FakeServers Servers { get; } = new(
        [
            Server(LinuxServer, ServerOperatingSystem.Linux),
            Server(OtherLinuxServer, ServerOperatingSystem.Linux),
            Server(MacServer, ServerOperatingSystem.MacOS),
            Server(AutoServer, ServerOperatingSystem.Auto) with
            {
                AuthenticationMethod = AuthenticationMethod.SshKey,
                PrivateKeyPath = "C:\\keys\\id_ed25519",
                CredentialReferenceId = null,
            },
            Server(NoCredentialServer, ServerOperatingSystem.Linux) with { CredentialReferenceId = null },
            Server(UnknownOsServer, ServerOperatingSystem.Unknown),
        ]);

        public InMemoryWorkloads Workloads { get; } = new();

        public RecordingTransport Transport { get; } = new();

        public RemoteReadRunner Runner { get; }

        public int BuildCount { get; private set; }

        private static Server Server(Guid id, ServerOperatingSystem operatingSystem) => new()
        {
            Id = id,
            Name = "server",
            Host = "server.example",
            Username = "tester",
            OperatingSystem = operatingSystem,
            AuthenticationMethod = AuthenticationMethod.Password,
            CredentialReferenceId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
        };
    }

    private sealed record Read(
        string Id,
        ServerOperatingSystem OperatingSystem,
        string Command,
        RemoteReadArgumentKind ArgumentKind) : IRemoteRead
    {
        public int OutputLimitBytes => 128 * 1024;

        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    }

    private sealed class RecordingTransport : IRemoteReadTransport
    {
        public List<(Server Server, RemoteReadExec Exec, TimeSpan ConnectionTimeout)> Calls { get; } = [];

        public RemoteReadTransportResult Result { get; set; } =
            new(SshConnectionErrorCode.None, new RemoteReadExecution(true, 0, "ok\n", false, false));

        public Task? Hold { get; set; }

        public Exception? Throw { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RemoteReadTransportResult> ExecuteAsync(
            Server server,
            RemoteReadExec exec,
            TimeSpan connectionTimeout,
            CancellationToken cancellationToken)
        {
            Calls.Add((server, exec, connectionTimeout));
            var hold = Hold;
            Started.TrySetResult();
            if (Throw is not null)
            {
                throw Throw;
            }

            if (hold is not null)
            {
                await hold.WaitAsync(cancellationToken);
            }

            return Result;
        }
    }

    private sealed class InMemoryWorkloads : IServerWorkloadStore
    {
        private readonly Dictionary<Guid, ServerWorkloadSnapshot> _snapshots = [];

        public event EventHandler<Guid>? WorkloadChanged
        {
            add { }
            remove { }
        }

        public ServerWorkloadSnapshot? Get(Guid serverId) => _snapshots.GetValueOrDefault(serverId);

        public IReadOnlyCollection<ServerWorkloadSnapshot> GetAll() => _snapshots.Values;

        public void Set(ServerWorkloadSnapshot snapshot) => _snapshots[snapshot.ServerId] = snapshot;

        public void Remove(Guid serverId) => _snapshots.Remove(serverId);
    }

    private sealed class FakeServers(IReadOnlyList<Server> servers) : IServerService
    {
        public event EventHandler? ServersChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(servers);

        public Task<ServerOperationResult> AddAsync(ServerInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServerOperationResult> AddAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServerOperationResult> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
