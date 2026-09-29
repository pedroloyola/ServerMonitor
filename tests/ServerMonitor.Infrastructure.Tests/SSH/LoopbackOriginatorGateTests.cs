using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ServerMonitor.Infrastructure.SSH;

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

    [Fact]
    public async Task A_connection_from_a_child_process_is_rejected_even_while_armed_and_does_not_consume_the_arm()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var boundPort = (uint)((IPEndPoint)listener.LocalEndpoint).Port;

        using var child = ChildProcess.ConnectAndHold(boundPort);
        using var acceptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var accepted = await listener.AcceptTcpClientAsync(acceptTimeout.Token);
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
    /// </summary>
    private sealed class ChildProcess : IDisposable
    {
        private readonly Process _process;
        private readonly DateTime _startTime;
        private readonly string _image;

        private ChildProcess(Process process)
        {
            _process = process;
            Pid = process.Id;
            _startTime = process.StartTime;
            _image = process.MainModule?.FileName ?? string.Empty;
        }

        public int Pid { get; }

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
                         $"$c = New-Object System.Net.Sockets.TcpClient; $c.Connect('127.0.0.1', {port}); Start-Sleep -Seconds 60"
                     })
            {
                info.ArgumentList.Add(argument);
            }

            return new ChildProcess(Process.Start(info) ?? throw new InvalidOperationException("child did not start"));
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
}
