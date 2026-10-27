using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using ServerMonitor.Infrastructure.SSH;
using Xunit.Abstractions;

namespace ServerMonitor.Infrastructure.Tests.SSH;

/// <summary>
/// M14-PJ-LOOP-1 C1/C2 against the REAL components: real loopback sockets, the real IPv4 TCP table
/// (<see cref="IpHelperTcpOwnerLookup"/>) and the production <see cref="LoopbackOriginatorGate"/> /
/// <see cref="SshJumpTunnel.IsVerifiedLoopbackListener"/>. A connection from a CHILD process is the
/// "another local process" of the threat model.
/// </summary>
public sealed class LoopbackOriginatorGateTests
{
    [Fact]
    public async Task A_connection_from_this_process_is_admitted_once_when_armed_and_rejected_when_sealed()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var boundPort = (uint)((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, (int)boundPort);
        using var accepted = await listener.AcceptTcpClientAsync();
        var originatorPort = (uint)((IPEndPoint)accepted.Client.RemoteEndPoint!).Port;
        var gate = new LoopbackOriginatorGate();

        var unarmed = gate.Evaluate("127.0.0.1", originatorPort, boundPort);
        gate.ArmNext();
        var armed = gate.Evaluate("127.0.0.1", originatorPort, boundPort);
        var afterAdmission = gate.Evaluate("127.0.0.1", originatorPort, boundPort);
        gate.ArmNext();
        gate.Seal();
        var sealedAgain = gate.Evaluate("127.0.0.1", originatorPort, boundPort);

        Assert.Equal(OriginatorRejection.NotArmed, unarmed.Rejection);
        Assert.True(armed.Admitted);
        Assert.Equal(Environment.ProcessId, armed.OwnerPid);
        Assert.False(afterAdmission.Admitted); // sealed automatically on admission
        Assert.Equal(OriginatorRejection.NotArmed, afterAdmission.Rejection);
        Assert.False(sealedAgain.Admitted);
    }

    // SVA-7 DIAGNOSTIC — temporary, never merge. Constructor + output helper exist only for the diagnostic.
    private readonly ITestOutputHelper _output;

    public LoopbackOriginatorGateTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public Task A_connection_from_a_child_process_is_rejected_even_while_armed_and_does_not_consume_the_arm()
        => RunChildScenarioAsync("fact");

    // SVA-7 DIAGNOSTIC — temporary, never merge. 40 repeats of the child scenario to raise the CI hit rate.
    public static IEnumerable<object[]> Sva7DiagnosticIterations => Enumerable.Range(1, 40).Select(i => new object[] { i });

    // SVA-7 DIAGNOSTIC — temporary, never merge.
    [Theory]
    [MemberData(nameof(Sva7DiagnosticIterations))]
    public Task SVA7_DIAGNOSTIC_child_scenario_repeat(int iteration) => RunChildScenarioAsync($"iter{iteration:D2}");

    private async Task RunChildScenarioAsync(string label)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var boundPort = (uint)((IPEndPoint)listener.LocalEndpoint).Port;

        using var child = ChildProcess.ConnectAndHold(boundPort);
        using var acceptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var accepted = await Sva7Diagnostic.AcceptChildOrFailAsync(listener, child, acceptTimeout.Token, label, _output);
        var originatorPort = (uint)((IPEndPoint)accepted.Client.RemoteEndPoint!).Port;
        var gate = new LoopbackOriginatorGate();

        var sealedDecision = gate.Evaluate("127.0.0.1", originatorPort, boundPort);
        Assert.False(sealedDecision.Admitted);
        Assert.Equal(child.Pid, sealedDecision.OwnerPid); // logged with the originator's PID

        gate.ArmNext();
        var decision = gate.Evaluate("127.0.0.1", originatorPort, boundPort);

        Assert.False(decision.Admitted);
        Assert.Equal(OriginatorRejection.ForeignOwner, decision.Rejection);
        Assert.Equal(child.Pid, decision.OwnerPid);
        Assert.True(gate.IsArmed);

        // Control: our own connection to the same listener is still admitted with the SAME arm.
        using var own = new TcpClient(AddressFamily.InterNetwork);
        await own.ConnectAsync(IPAddress.Loopback, (int)boundPort);
        using var ownAccepted = await listener.AcceptTcpClientAsync(acceptTimeout.Token);
        var ownPort = (uint)((IPEndPoint)ownAccepted.Client.RemoteEndPoint!).Port;
        Assert.True(gate.Evaluate("127.0.0.1", ownPort, boundPort).Admitted);
    }

    [Fact]
    public void Unknown_originators_non_loopback_hosts_and_lookup_failures_fail_closed()
    {
        var gate = new LoopbackOriginatorGate();
        gate.ArmNext();
        Assert.Equal(OriginatorRejection.OwnerNotFound, gate.Evaluate("127.0.0.1", 1, 2).Rejection);
        foreach (var host in new[] { "::1", "10.0.0.1", "0.0.0.0", "localhost", "", null })
        {
            Assert.Equal(OriginatorRejection.NotLoopback, gate.Evaluate(host, 50000, 40000).Rejection);
        }

        Assert.Equal(OriginatorRejection.NotLoopback, gate.Evaluate("127.0.0.1", 50000, 0).Rejection);

        var failing = new LoopbackOriginatorGate(new ThrowingLookup(), Environment.ProcessId);
        failing.ArmNext();
        Assert.Equal(OriginatorRejection.LookupFailed, failing.Evaluate("127.0.0.1", 50000, 40000).Rejection);
        Assert.True(failing.IsArmed); // a failed lookup never consumes the arm
        Assert.True(gate.IsArmed);
    }

    [Fact]
    public void Only_a_real_ipv4_loopback_listener_owned_by_this_process_passes_the_bind_proof()
    {
        using var v4 = new TcpListener(IPAddress.Loopback, 0);
        v4.Start();
        var v4Port = (uint)((IPEndPoint)v4.LocalEndpoint).Port;
        using var v6 = new TcpListener(IPAddress.IPv6Loopback, 0);
        v6.Start();
        var v6Port = (uint)((IPEndPoint)v6.LocalEndpoint).Port;
        using var any = new TcpListener(IPAddress.Any, 0);
        any.Start();
        var anyPort = (uint)((IPEndPoint)any.LocalEndpoint).Port;
        var lookup = IpHelperTcpOwnerLookup.Instance;

        Assert.True(SshJumpTunnel.IsVerifiedLoopbackListener("127.0.0.1", v4Port, lookup, Environment.ProcessId));
        Assert.False(SshJumpTunnel.IsVerifiedLoopbackListener("127.0.0.1", v4Port, lookup, Environment.ProcessId + 1));
        Assert.False(SshJumpTunnel.IsVerifiedLoopbackListener("127.0.0.1", v6Port, lookup, Environment.ProcessId));
        Assert.False(SshJumpTunnel.IsVerifiedLoopbackListener("127.0.0.1", anyPort, lookup, Environment.ProcessId));
        Assert.False(SshJumpTunnel.IsVerifiedLoopbackListener("localhost", v4Port, lookup, Environment.ProcessId));
        Assert.False(SshJumpTunnel.IsVerifiedLoopbackListener("::1", v6Port, lookup, Environment.ProcessId));
        Assert.False(SshJumpTunnel.IsVerifiedLoopbackListener("127.0.0.1", 0, lookup, Environment.ProcessId));
    }

    private sealed class ThrowingLookup : ITcpOwnerLookup
    {
        public int? FindLoopbackConnectionOwner(int localPort, int remotePort) => throw new InvalidOperationException();

        public bool IsLoopbackListenerOwnedBy(int port, int processId) => throw new InvalidOperationException();
    }

    /// <summary>
    /// A child Windows PowerShell that connects to <c>127.0.0.1:port</c> and holds the socket. Identity (PID,
    /// image, start time) is recorded at start and revalidated before the exact PID is stopped (BOSS §17).
    /// SVA-7 DIAGNOSTIC — temporary, never merge: stdout/stderr are drained asynchronously and every line is
    /// stamped with ms since the Process.Start call. CAVEAT: draining alone can change behaviour (a child that
    /// previously blocked on a full, unread pipe no longer blocks), so a run with zero timeouts does not by
    /// itself prove hypothesis A or B wrong.
    /// </summary>
    private sealed class ChildProcess : IDisposable
    {
        private const string ExitedMarker = "\u0000EXITED";
        private readonly Process _process;
        private readonly DateTime _startTime;
        private readonly string _image;
        private readonly Stopwatch _clock;
        private readonly object _gate = new();
        private readonly List<(long Ms, string Line)> _stdout = new();
        private readonly List<(long Ms, string Line)> _stderr = new();
        private long? _exitedMs;

        private ChildProcess(Process process, Stopwatch clock, long startReturnedMs)
        {
            _process = process;
            _clock = clock;
            StartReturnedMs = startReturnedMs;
            Pid = process.Id;
            _startTime = process.StartTime;
            _image = process.MainModule?.FileName ?? string.Empty;
            IdentityCapturedMs = clock.ElapsedMilliseconds;
        }

        public int Pid { get; }

        public long StartReturnedMs { get; }

        public long IdentityCapturedMs { get; }

        public long ElapsedMs => _clock.ElapsedMilliseconds;

        public static ChildProcess ConnectAndHold(uint port)
        {
            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var info = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-Command",
                         "[Console]::Out.WriteLine('SVA7 PS_READY'); "
                         + $"$c = New-Object System.Net.Sockets.TcpClient; $c.Connect('127.0.0.1', {port}); "
                         + "[Console]::Out.WriteLine('SVA7 CONNECTED'); Start-Sleep -Seconds 60"
                     })
            {
                info.ArgumentList.Add(argument);
            }

            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var clock = new Stopwatch();
            ChildProcess? child = null;
            var early = new List<(bool IsError, long Ms, string Line)>();
            process.OutputDataReceived += (_, e) => Record(isError: false, e.Data);
            process.ErrorDataReceived += (_, e) => Record(isError: true, e.Data);
            process.Exited += (_, _) => Record(isError: false, ExitedMarker);

            clock.Start();
            if (!process.Start())
            {
                throw new InvalidOperationException("child did not start");
            }

            var startReturnedMs = clock.ElapsedMilliseconds;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var created = new ChildProcess(process, clock, startReturnedMs);
            lock (early)
            {
                foreach (var (isError, ms, line) in early)
                {
                    created.Append(isError, ms, line);
                }

                early.Clear();
                child = created;
            }

            return created;

            void Record(bool isError, string? data)
            {
                if (data is null)
                {
                    return;
                }

                var ms = clock.ElapsedMilliseconds;
                ChildProcess? current;
                lock (early)
                {
                    current = child;
                    if (current is null)
                    {
                        early.Add((isError, ms, data));
                        return;
                    }
                }

                current.Append(isError, ms, data);
            }
        }

        private void Append(bool isError, long ms, string line)
        {
            lock (_gate)
            {
                if (line == ExitedMarker)
                {
                    _exitedMs ??= ms;
                }
                else
                {
                    (isError ? _stderr : _stdout).Add((ms, line));
                }
            }
        }

        public ChildSnapshot Snapshot()
        {
            bool hasExited;
            int? exitCode = null;
            try
            {
                hasExited = _process.HasExited;
                if (hasExited)
                {
                    exitCode = _process.ExitCode;
                }
            }
            catch (InvalidOperationException)
            {
                hasExited = false;
            }

            lock (_gate)
            {
                return new ChildSnapshot(hasExited, exitCode, _exitedMs, _stdout.ToArray(), _stderr.ToArray());
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    using var current = Process.GetProcessById(Pid);
                    if (current.StartTime == _startTime
                        && string.Equals(current.MainModule?.FileName, _image, StringComparison.OrdinalIgnoreCase))
                    {
                        _process.Kill(entireProcessTree: false);
                        _process.WaitForExit(5000);
                    }
                }
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
            finally
            {
                _process.Dispose();
            }
        }
    }

    private sealed record ChildSnapshot(
        bool HasExited,
        int? ExitCode,
        long? ExitedMs,
        IReadOnlyList<(long Ms, string Line)> Stdout,
        IReadOnlyList<(long Ms, string Line)> Stderr)
    {
        public long? MarkerMs(string marker)
        {
            foreach (var (ms, line) in Stdout)
            {
                if (line.Contains(marker, StringComparison.Ordinal))
                {
                    return ms;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// SVA-7 DIAGNOSTIC — temporary, never merge. Discriminates (A) slow PowerShell 5.1 cold start from (B) a child
    /// that failed silently, plus (C) stalls on either side of an already-started child. Grep CI logs for
    /// <c>SVA7-DIAG</c>. All times are ms since the child's Process.Start call.
    /// </summary>
    private static class Sva7Diagnostic
    {
        private const int MaxStreamChars = 2048;
        private static readonly TimeSpan LateAcceptWindow = TimeSpan.FromSeconds(60);
        private static readonly object SamplesGate = new();
        private static readonly List<long> AcceptSamplesMs = new();

        public static async Task<TcpClient> AcceptChildOrFailAsync(
            TcpListener listener,
            ChildProcess child,
            CancellationToken budget,
            string label,
            ITestOutputHelper output)
        {
            var budgetStartMs = child.ElapsedMs;
            var acceptTask = listener.AcceptTcpClientAsync(CancellationToken.None).AsTask();
            _ = acceptTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var budgetTask = Task.Delay(Timeout.Infinite, budget);
            _ = budgetTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (await Task.WhenAny(acceptTask, budgetTask) == acceptTask)
            {
                var acceptMs = child.ElapsedMs;
                lock (SamplesGate)
                {
                    AcceptSamplesMs.Add(acceptMs);
                }

                var ok = child.Snapshot();
                var line = $"SVA7-DIAG OK {label} startReturned={child.StartReturnedMs}ms "
                           + $"identity={child.IdentityCapturedMs}ms budgetStart={budgetStartMs}ms "
                           + $"psReady={Fmt(ok.MarkerMs("SVA7 PS_READY"))} "
                           + $"childConnected={Fmt(ok.MarkerMs("SVA7 CONNECTED"))} "
                           + $"accept={acceptMs}ms stderrLines={ok.Stderr.Count}";
                output.WriteLine(line);
                AppendStepSummary(line);
                return await acceptTask;
            }

            // Budget expired: freeze the state at the moment of the timeout, then keep listening to see whether a
            // late connection ever arrives (A) or never does (B/C).
            var timerFiredMs = child.ElapsedMs;
            var atTimeout = child.Snapshot();
            ThreadPool.GetAvailableThreads(out var availableWorkers, out _);
            ThreadPool.GetMaxThreads(out var maxWorkers, out _);
            var threadPool = $"tp.threads={ThreadPool.ThreadCount} tp.pending={ThreadPool.PendingWorkItemCount} "
                             + $"tp.busyWorkers={maxWorkers - availableWorkers}";

            string lateAccept;
            if (await Task.WhenAny(acceptTask, Task.Delay(LateAcceptWindow)) == acceptTask
                && acceptTask.Status == TaskStatus.RanToCompletion)
            {
                lateAccept = $"{child.ElapsedMs}ms";
                acceptTask.Result.Dispose();
            }
            else
            {
                lateAccept = $"none-within+{(int)LateAcceptWindow.TotalMilliseconds}ms";
            }

            var final = child.Snapshot();
            var hint = Classify(atTimeout, final, timerFiredMs);
            var message = $"SVA7-DIAG TIMEOUT {label} hint={hint} budget=30000ms budgetStart={budgetStartMs}ms "
                          + $"timerFired={timerFiredMs}ms startReturned={child.StartReturnedMs}ms "
                          + $"identity={child.IdentityCapturedMs}ms "
                          + $"hasExitedAtTimeout={atTimeout.HasExited} exitCodeAtTimeout={Fmt(atTimeout.ExitCode)} "
                          + $"exitedAt={Fmt(final.ExitedMs)} psReady={Fmt(final.MarkerMs("SVA7 PS_READY"))} "
                          + $"childConnected={Fmt(final.MarkerMs("SVA7 CONNECTED"))} lateAccept={lateAccept} "
                          + $"{threadPool} prior={PriorSummary()} "
                          + $"| stdout=[{Render(final.Stdout)}] | stderr=[{Render(final.Stderr)}]";
            output.WriteLine(message);
            AppendStepSummary(message);
            Assert.Fail(message);
            throw new UnreachableException();
        }

        private static string Classify(ChildSnapshot atTimeout, ChildSnapshot final, long timerFiredMs)
        {
            if (atTimeout.HasExited)
            {
                return "B_CHILD_EXITED";
            }

            // PowerShell 5.1 may emit benign CLIXML progress ("Preparing modules for first use") on a redirected stderr.
            if (final.Stderr.Any(e => e.Line.Trim().Length > 0
                                      && !e.Line.StartsWith("#< CLIXML", StringComparison.Ordinal)
                                      && !e.Line.Contains("Preparing modules", StringComparison.OrdinalIgnoreCase)))
            {
                return "B_CHILD_STDERR";
            }

            if (final.MarkerMs("SVA7 CONNECTED") is { } connected && connected <= timerFiredMs)
            {
                return "C_PARENT_ACCEPT_STALL";
            }

            var psReady = final.MarkerMs("SVA7 PS_READY");
            if (psReady is { } ready && ready <= timerFiredMs)
            {
                return "C_CHILD_CONNECT_STALL";
            }

            return psReady is null ? "A_SLOW_START_NEVER_READY" : "A_SLOW_START_READY_AFTER_BUDGET";
        }

        private static string PriorSummary()
        {
            long[] samples;
            lock (SamplesGate)
            {
                samples = AcceptSamplesMs.Order().ToArray();
            }

            if (samples.Length == 0)
            {
                return "n=0";
            }

            long At(double q) => samples[Math.Clamp((int)Math.Ceiling(q * samples.Length) - 1, 0, samples.Length - 1)];
            return $"n={samples.Length},p50={At(0.5)}ms,p95={At(0.95)}ms,max={samples[^1]}ms";
        }

        private static string Render(IReadOnlyList<(long Ms, string Line)> lines)
        {
            var builder = new StringBuilder();
            foreach (var (ms, line) in lines)
            {
                builder.Append('+').Append(ms).Append("ms ").Append(Sanitize(line)).Append(" ¶ ");
                if (builder.Length > MaxStreamChars)
                {
                    break;
                }
            }

            return builder.Length > MaxStreamChars
                ? builder.ToString(0, MaxStreamChars) + "…[truncated]"
                : builder.ToString();
        }

        /// <summary>No runner usernames, hostnames, profile/temp paths or token-shaped strings reach the CI log.</summary>
        private static string Sanitize(string text)
        {
            var result = new string(text.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
            foreach (var (secret, placeholder) in new[]
                     {
                         (Environment.GetEnvironmentVariable("USERPROFILE"), "<profile>"),
                         (Environment.GetEnvironmentVariable("TEMP"), "<temp>"),
                         (Environment.GetEnvironmentVariable("TMP"), "<temp>"),
                         (Environment.GetEnvironmentVariable("GITHUB_WORKSPACE"), "<workspace>"),
                         (Environment.UserName, "<user>"),
                         (Environment.UserDomainName, "<domain>"),
                         (Environment.MachineName, "<host>")
                     })
            {
                if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 3)
                {
                    result = result.Replace(secret, placeholder, StringComparison.OrdinalIgnoreCase);
                }
            }

            result = Regex.Replace(result, @"(?i)[a-z]:\\Users\\[^\\\s'""]+", @"<drive>:\Users\<user>");
            return Regex.Replace(
                result,
                @"\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|eyJ[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-\.]+)",
                "<redacted>");
        }

        private static void AppendStepSummary(string line)
        {
            // `dotnet test` does not print passed-test output at its default verbosity, so on CI the latency
            // distribution also goes to the job summary. Only sanitized markers and numbers are written.
            var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                File.AppendAllText(path, $"`{line.Replace('`', '\'')}`  {Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string Fmt(long? ms) => ms is { } value ? $"{value}ms" : "none";

        private static string Fmt(int? code) => code?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
    }
}
