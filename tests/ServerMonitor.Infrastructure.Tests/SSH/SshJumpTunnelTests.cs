using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.SSH;
using static ServerMonitor.Infrastructure.Tests.SSH.SshRoutedTestDoubles;

namespace ServerMonitor.Infrastructure.Tests.SSH;

/// <summary>
/// M14.4b-2 §1/C5 (+ the C1–C3 wiring): the production <see cref="SshJumpTunnel"/> owner with fake SSH.NET parts.
/// Teardown order, idempotence and teardown-on-failure are asserted on the REAL owner.
/// </summary>
public sealed class SshJumpTunnelTests
{
    private static readonly SshEndpoint Target = SshEndpoint.Create("10.0.0.5", 22);

    [Fact]
    public async Task Teardown_is_targets_then_forward_then_jump_and_is_idempotent()
    {
        var parts = new Parts();
        var tunnel = parts.Tunnel();
        Assert.True((await tunnel.OpenAsync(_ => true, default)).IsOpen);
        tunnel.CreateTargetSession("tester", SshLogin.None, TimeSpan.FromSeconds(1));
        tunnel.CreateTargetSession("tester", new SshLogin(SshLoginKind.Password, Password: "x"), TimeSpan.FromSeconds(1));
        parts.Log.Events.Clear();

        await tunnel.DisposeAsync();
        await tunnel.DisposeAsync();

        Assert.Equal(["target.dispose", "target.dispose", "forward.stop", "forward.dispose", "jump.dispose"], parts.Log.Events);
    }

    [Fact]
    public async Task A_jump_error_latches_the_jump_as_dropped_even_while_IsConnected_still_lags()
    {
        var parts = new Parts();
        var tunnel = parts.Tunnel();
        await tunnel.OpenAsync(_ => true, default);
        Assert.True(tunnel.IsJumpConnected);

        parts.Jump!.RaiseFault(); // SSH.NET's ErrorOccurred; the client's IsConnected has not caught up yet
        Assert.True(parts.Jump.IsConnected);

        Assert.False(tunnel.IsJumpConnected);
        parts.Jump.RaiseFault();
        Assert.False(tunnel.IsJumpConnected); // sticky
    }

    [Fact]
    public async Task Rejection_warnings_are_capped_per_tunnel_and_the_rest_are_summarised_at_teardown()
    {
        var parts = new Parts { BoundPort = 45678 };
        parts.Lookup.ConnectionOwner = 4242;
        var tunnel = (SshJumpTunnel)parts.Tunnel();
        await tunnel.OpenAsync(_ => true, default);

        for (var i = 0; i < 20; i++)
        {
            Assert.Throws<TunnelOriginatorRejectedException>(() => parts.Forward!.Handler!("127.0.0.1", (uint)(50000 + i)));
        }

        var individual = parts.Logger.Warnings.Where(w => w.Contains("rejected a loopback connection")).ToList();
        Assert.Equal(SshJumpTunnel.IndividuallyLoggedRejections, individual.Count);
        Assert.All(individual, w => Assert.Contains("owner PID 4242", w));
        Assert.Contains("originator port 50000", individual[0]);

        await tunnel.DisposeAsync();

        var summary = Assert.Single(parts.Logger.Warnings, w => w.Contains("further loopback connections"));
        Assert.Contains("15 further", summary);
        Assert.Contains("total 20", summary);
        Assert.Equal(SshJumpTunnel.IndividuallyLoggedRejections + 1, parts.Logger.Warnings.Count);
    }

    [Fact]
    public async Task A_few_rejections_produce_no_teardown_summary()
    {
        var parts = new Parts { BoundPort = 45678 };
        var tunnel = parts.Tunnel();
        await tunnel.OpenAsync(_ => true, default);
        Assert.Throws<TunnelOriginatorRejectedException>(() => parts.Forward!.Handler!("127.0.0.1", 50000));

        await tunnel.DisposeAsync();

        Assert.Single(parts.Logger.Warnings);
    }

    [Fact]
    public async Task A_failed_jump_connect_creates_no_forward_and_still_releases_the_jump()
    {
        var parts = new Parts { JumpResult = new SshSessionResult { ErrorCode = SshConnectionErrorCode.AuthenticationFailed } };
        var tunnel = parts.Tunnel();

        var open = await tunnel.OpenAsync(_ => true, default);
        await tunnel.DisposeAsync();

        Assert.Equal(JumpTunnelOpenStage.Jump, open.Stage);
        Assert.Null(parts.Forward);
        Assert.Equal(["jump.connect", "jump.dispose"], parts.Log.Events);
        Assert.Throws<InvalidOperationException>(() => new Parts().Tunnel().CreateTargetSession("t", SshLogin.None, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("127.0.0.1", 40000u, false)] // listener not proved by the TCP table
    [InlineData("::1", 40000u, true)]        // "localhost" lands here
    [InlineData("localhost", 40000u, true)]
    [InlineData("0.0.0.0", 40000u, true)]
    [InlineData("127.0.0.1", 0u, true)]
    public async Task A_listener_that_is_not_a_proved_ipv4_loopback_is_a_tunnel_listen_failure_and_is_torn_down(
        string boundHost,
        uint boundPort,
        bool tableSaysOwned)
    {
        var parts = new Parts { BoundHost = boundHost, BoundPort = boundPort };
        parts.Lookup.ListenerOwned = tableSaysOwned;
        var tunnel = parts.Tunnel();

        var open = await tunnel.OpenAsync(_ => true, default);
        await tunnel.DisposeAsync();

        Assert.Equal(JumpTunnelOpenStage.TunnelListen, open.Stage);
        Assert.Contains("forward.stop", parts.Log.Events);
        Assert.Equal("jump.dispose", parts.Log.Events[^1]);
        Assert.Throws<ObjectDisposedException>(() => tunnel.CreateTargetSession("t", SshLogin.None, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task The_forward_binds_the_literal_loopback_and_targets_the_logical_endpoint()
    {
        var parts = new Parts();
        var tunnel = parts.Tunnel();

        await tunnel.OpenAsync(_ => true, default);

        Assert.Equal(("127.0.0.1", "10.0.0.5", 22u), parts.ForwardRequest);
        Assert.Equal("127.0.0.1", SshJumpTunnel.LoopbackHost);
        Assert.NotNull(parts.Forward!.Handler); // subscribed as part of Start, i.e. before the listener exists
        Assert.Equal(1, parts.Forward.StartCount);
    }

    [Fact]
    public async Task Target_sessions_dial_the_literal_loopback_and_exactly_the_bound_port_through_the_gate()
    {
        var parts = new Parts { BoundPort = 45678 };
        var tunnel = parts.Tunnel();
        await tunnel.OpenAsync(_ => true, default);

        tunnel.CreateTargetSession("tester", SshLogin.None, TimeSpan.FromSeconds(1));

        var (dial, gate) = Assert.Single(parts.TargetDials);
        Assert.Equal(new SshDialTarget("127.0.0.1", 45678, "tester"), dial);
        Assert.Same(((SshJumpTunnel)tunnel).Gate, gate);
        Assert.Equal(new SshDialTarget("127.0.0.1", 45678, "u"), SshJumpTunnel.TargetDial(45678, "u"));
        Assert.Throws<ArgumentOutOfRangeException>(() => SshJumpTunnel.TargetDial(0, "u"));
    }

    [Fact]
    public async Task The_request_handler_rejects_by_throwing_unless_the_gate_admits()
    {
        var parts = new Parts { BoundPort = 45678 };
        var tunnel = (SshJumpTunnel)parts.Tunnel();
        await tunnel.OpenAsync(_ => true, default);
        parts.Lookup.ConnectionOwner = Environment.ProcessId;

        // Unarmed: rejected.
        Assert.Throws<TunnelOriginatorRejectedException>(() => parts.Forward!.Handler!("127.0.0.1", 50000));

        // Armed + ours: admitted once, then sealed.
        tunnel.Gate.ArmNext();
        parts.Forward!.Handler!("127.0.0.1", 50000);
        Assert.Equal((50000, 45678), parts.Lookup.LastQuery);
        Assert.Throws<TunnelOriginatorRejectedException>(() => parts.Forward.Handler!("127.0.0.1", 50001));

        // Armed + foreign owner: rejected, and the arm is NOT consumed by the foreign attempt.
        tunnel.Gate.ArmNext();
        parts.Lookup.ConnectionOwner = Environment.ProcessId + 1;
        Assert.Throws<TunnelOriginatorRejectedException>(() => parts.Forward.Handler!("127.0.0.1", 50002));
        Assert.True(tunnel.Gate.IsArmed);
    }

    internal sealed class Parts
    {
        public EventLog Log { get; } = new();

        public FakeLookup Lookup { get; } = new();

        public CapturingLogger Logger { get; } = new();

        public FakeJump? Jump { get; private set; }

        public SshSessionResult JumpResult { get; set; } = new() { ErrorCode = SshConnectionErrorCode.None, IdentificationReceived = true };

        public string BoundHost { get; set; } = "127.0.0.1";

        public uint BoundPort { get; set; } = 40000;

        public FakeForward? Forward { get; private set; }

        public (string Bind, string Host, uint Port)? ForwardRequest { get; private set; }

        public List<(SshDialTarget Dial, ISshConnectGate? Gate)> TargetDials { get; } = [];

        public IJumpTunnel Tunnel() => new SshJumpTunnel(Jump = new FakeJump(this), Target, new FakeTargets(this), Logger, Lookup, Environment.ProcessId);

        internal sealed class FakeJump(Parts parts) : IJumpClient
        {
            public event Action? Faulted;

            public bool IsConnected => true;

            public void RaiseFault() => Faulted?.Invoke();

            public Task<SshSessionResult> ConnectAsync(Func<HostKeyIdentity, bool> hostKeyVerifier, CancellationToken cancellationToken)
            {
                parts.Log.Add("jump.connect");
                return Task.FromResult(parts.JumpResult);
            }

            public ILocalForward CreateLocalForward(string bindHost, string targetHost, uint targetPort)
            {
                parts.ForwardRequest = (bindHost, targetHost, targetPort);
                return parts.Forward = new FakeForward(parts);
            }

            public void Dispose() => parts.Log.Add("jump.dispose");
        }

        private sealed class FakeTargets(Parts parts) : ISshDialSessionFactory
        {
            public ISshSession Create(SshDialTarget dial, SshLogin login, TimeSpan timeout, ISshConnectGate? gate)
            {
                parts.TargetDials.Add((dial, gate));
                return new DisposeLogged(parts.Log);
            }
        }

        private sealed class DisposeLogged(EventLog log) : ISshSession
        {
            public Task<SshSessionResult> ConnectAsync(Func<HostKeyIdentity, bool> v, CancellationToken c) => throw new NotSupportedException();

            public Task<SshSessionResult> DetectOperatingSystemAsync(Func<HostKeyIdentity, bool> v, CancellationToken c) => throw new NotSupportedException();

            public Task<SshSessionResult> CollectLinuxMetricsAsync(Func<HostKeyIdentity, bool> v, TimeSpan i, CancellationToken c) => throw new NotSupportedException();

            public Task<SshSessionResult> CollectMacOsMetricsAsync(Func<HostKeyIdentity, bool> v, CancellationToken c) => throw new NotSupportedException();

            public Task<SshSessionResult> CollectWorkloadsAsync(Func<HostKeyIdentity, bool> v, WorkloadCollectionPlan p, CancellationToken c) =>
                throw new NotSupportedException();

            public void Dispose() => log.Add("target.dispose");
        }
    }

    internal sealed class FakeForward(Parts parts) : ILocalForward
    {
        public Action<string, uint>? Handler { get; private set; }

        public int StartCount { get; private set; }

        public string BoundHost => parts.BoundHost;

        public uint BoundPort => StartCount > 0 ? parts.BoundPort : 0;

        public bool IsStarted { get; private set; }

        public void Start(Action<string, uint> onRequest)
        {
            Handler = onRequest;
            StartCount++;
            IsStarted = true;
            parts.Log.Add("forward.start");
        }

        public void Stop()
        {
            IsStarted = false;
            parts.Log.Add("forward.stop");
        }

        public void Dispose() => parts.Log.Add("forward.dispose");
    }

    internal sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    internal sealed class FakeLookup : ITcpOwnerLookup
    {
        public bool ListenerOwned { get; set; } = true;

        public int? ConnectionOwner { get; set; }

        public (int Local, int Remote)? LastQuery { get; private set; }

        public int? FindLoopbackConnectionOwner(int localPort, int remotePort)
        {
            LastQuery = (localPort, remotePort);
            return ConnectionOwner;
        }

        public bool IsLoopbackListenerOwnedBy(int port, int processId) => ListenerOwned;
    }
}
