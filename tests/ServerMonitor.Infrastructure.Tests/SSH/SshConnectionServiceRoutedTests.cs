using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.SSH;
using static ServerMonitor.Infrastructure.Tests.SSH.SshRoutedTestDoubles;

namespace ServerMonitor.Infrastructure.Tests.SSH;

/// <summary>
/// M14.4b-2 §4: the production <see cref="SshConnectionService"/> routed pipeline — probe-before-auth on BOTH
/// hops, per-hop store selection by the LOGICAL identity, independent jump/target credentials, and the tunnel
/// torn down on every path. Only the network (sessions, tunnel) is a double.
/// </summary>
public sealed class SshConnectionServiceRoutedTests
{
    private static readonly SshEndpoint JumpEndpoint = SshEndpoint.Create("bastion.example", 2222);
    private static readonly SshEndpoint TargetEndpoint = SshEndpoint.Create("10.0.0.5", 22);
    private static readonly SshRoute Route = SshRoute.Create(JumpEndpoint, TargetEndpoint);
    private static readonly HostKeyIdentity JumpKey = Key(1);
    private static readonly HostKeyIdentity TargetKey = Key(2);

    [Fact]
    public async Task Success_verifies_each_hop_before_its_credential_and_tears_the_tunnel_down()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.JumpProbe(JumpKey);
        f.Target(TargetKey, SshConnectionErrorCode.AuthenticationFailed); // none-auth probe
        f.Target(TargetKey, SshConnectionErrorCode.None);                 // authenticated session

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [
                $"direct.get {JumpEndpoint}",
                "jump.probe.connect",
                "cred.read JumpPassword",
                "tunnel.create Password",
                "tunnel.open",
                $"routed.get {Route}",
                "target.probe.connect",
                "cred.read Password",
                "target.Password.connect",
                "tunnel.dispose"
            ],
            f.Log.Events);
        var tunnel = Assert.Single(f.Tunnels.Created);
        Assert.Equal(new SshDialTarget("bastion.example", 2222, "jumper"), tunnel.JumpDial);
        Assert.Equal("jump-secret", tunnel.JumpLogin.Password);
        Assert.Equal(TargetEndpoint, tunnel.Target);
        Assert.Equal(["tester", "tester"], tunnel.TargetSessions.Select(s => s.Username));
        Assert.Equal("target-secret", tunnel.TargetSessions[1].Login.Password);
        Assert.Equal([JumpEndpoint], f.Direct.Lookups);
        Assert.Equal([Route], f.Routed.Lookups);
        Assert.Empty(f.Direct.Writes);
        Assert.Empty(f.Routed.Writes);
    }

    [Fact]
    public async Task Unknown_jump_key_is_reported_for_the_jump_before_any_credential_or_tunnel()
    {
        var f = new Fixture();
        f.JumpProbe(JumpKey);

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(SshConnectionErrorCode.JumpHostKeyUnknown, result.ErrorCode);
        Assert.Equal(ServerConnectionState.HostKeyUnknown, result.State);
        Assert.Equal(SshHostKeyHop.Jump, result.HostKeyHop);
        Assert.Equal(JumpEndpoint, result.HostKeyEndpoint);
        Assert.Null(result.HostKeyRoute);
        Assert.Equal(JumpKey, result.PresentedHostKey);
        Assert.Empty(f.Credentials.Reads);
        Assert.Empty(f.Tunnels.Created);
        Assert.Empty(f.Routed.Lookups);
        Assert.Empty(f.Direct.Writes);
        Assert.Empty(f.Routed.Writes);
    }

    [Fact]
    public async Task Changed_jump_key_is_a_jump_mismatch_before_any_credential_or_tunnel()
    {
        var f = new Fixture().TrustJump(Key(7));
        f.JumpProbe(JumpKey);

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(SshConnectionErrorCode.JumpHostKeyMismatch, result.ErrorCode);
        Assert.Equal(SshHostKeyHop.Jump, result.HostKeyHop);
        Assert.Equal(Key(7), result.TrustedHostKey!.Identity);
        Assert.Empty(f.Credentials.Reads);
        Assert.Empty(f.Tunnels.Created);
    }

    [Fact]
    public async Task Jump_key_changing_between_probe_and_auth_is_a_jump_mismatch_and_no_target_is_dialled()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.JumpProbe(JumpKey);
        f.Tunnels.JumpKey = Key(8);

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(SshConnectionErrorCode.JumpHostKeyMismatch, result.ErrorCode);
        Assert.Equal(ServerConnectionState.HostKeyMismatch, result.State);
        Assert.Equal(SshHostKeyHop.Jump, result.HostKeyHop);   // the panel names the JUMP, not the target
        Assert.Equal(JumpEndpoint, result.HostKeyEndpoint);
        Assert.Null(result.HostKeyRoute);
        Assert.Equal(Key(8), result.PresentedHostKey);
        Assert.Equal(JumpKey, result.TrustedHostKey!.Identity);
        Assert.Empty(f.Direct.Writes);
        Assert.Empty(f.Routed.Writes);
        Assert.Empty(Assert.Single(f.Tunnels.Created).TargetSessions);
        Assert.True(f.Tunnels.Created[0].Disposed);
        Assert.DoesNotContain(f.Credentials.Reads, r => r.Kind == ServerCredentialKind.Password);
    }

    [Fact]
    public async Task Unknown_target_key_is_reported_for_the_route_and_the_target_credential_is_never_read()
    {
        var f = new Fixture().TrustJump();
        f.JumpProbe(JumpKey);
        f.Target(TargetKey, SshConnectionErrorCode.AuthenticationFailed);

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(SshConnectionErrorCode.RoutedHostKeyUnknown, result.ErrorCode);
        Assert.Equal(ServerConnectionState.HostKeyUnknown, result.State);
        Assert.Equal(SshHostKeyHop.Target, result.HostKeyHop);
        Assert.Equal(Route, result.HostKeyRoute);
        Assert.Null(result.HostKeyEndpoint);
        Assert.Equal(TargetKey, result.PresentedHostKey);
        Assert.Equal([ServerCredentialKind.JumpPassword], f.Credentials.Reads.Select(r => r.Kind));
        Assert.Single(Assert.Single(f.Tunnels.Created).TargetSessions); // the probe only
        Assert.True(f.Tunnels.Created[0].Disposed);
        Assert.Empty(f.Direct.Writes);
        Assert.Empty(f.Routed.Writes);
    }

    [Fact]
    public async Task Changed_target_key_is_a_routed_mismatch_and_the_target_credential_is_never_read()
    {
        var f = new Fixture().TrustJump().TrustTarget(Key(9));
        f.JumpProbe(JumpKey);
        f.Target(TargetKey, SshConnectionErrorCode.AuthenticationFailed);

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(SshConnectionErrorCode.RoutedHostKeyMismatch, result.ErrorCode);
        Assert.Equal(ServerConnectionState.HostKeyMismatch, result.State);
        Assert.Equal(SshHostKeyHop.Target, result.HostKeyHop);
        Assert.Equal(Key(9), result.TrustedRoutedHostKey!.Identity);
        Assert.DoesNotContain(f.Credentials.Reads, r => r.Kind == ServerCredentialKind.Password);
    }

    [Fact]
    public async Task Target_key_is_looked_up_by_the_logical_route_never_by_a_direct_or_loopback_entry()
    {
        // The target's real key IS trusted — but only in the DIRECT store, under its bare endpoint and under
        // the loopback. Through a jump that must count for nothing.
        var f = new Fixture().TrustJump();
        f.Direct.Entries[TargetEndpoint] = new TrustedHostKey { Endpoint = TargetEndpoint, Identity = TargetKey };
        var loopback = SshEndpoint.Create("127.0.0.1", 22);
        f.Direct.Entries[loopback] = new TrustedHostKey { Endpoint = loopback, Identity = TargetKey };
        f.Routed.Entries[SshRoute.Create(JumpEndpoint, loopback)] = Routed(SshRoute.Create(JumpEndpoint, loopback), TargetKey);
        f.JumpProbe(JumpKey);
        f.Target(TargetKey, SshConnectionErrorCode.AuthenticationFailed);

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(SshConnectionErrorCode.RoutedHostKeyUnknown, result.ErrorCode);
        Assert.Equal([JumpEndpoint], f.Direct.Lookups);
        Assert.Equal([Route], f.Routed.Lookups);
    }

    [Fact]
    public async Task Jump_and_target_overrides_are_independent_and_stored_secrets_are_not_read_when_overridden()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.JumpProbe(JumpKey);
        f.Target(TargetKey, SshConnectionErrorCode.AuthenticationFailed);
        f.Target(TargetKey, SshConnectionErrorCode.None);
        var request = f.Request(credentialOverride: new SecretValue("typed-target"), jumpOverride: new SecretValue("typed-jump"));

        var result = await f.Service.TestConnectionAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Empty(f.Credentials.Reads);
        Assert.Equal("typed-jump", f.Tunnels.Created[0].JumpLogin.Password);
        Assert.Equal("typed-target", f.Tunnels.Created[0].TargetSessions[1].Login.Password);
    }

    [Fact]
    public async Task Jump_private_key_uses_the_jump_passphrase_kind_never_the_targets()
    {
        var f = new Fixture(jump => jump with
        {
            AuthenticationMethod = AuthenticationMethod.SshKey,
            PrivateKeyPath = "C:\\keys\\jump"
        }).TrustJump().TrustTarget();
        f.Credentials.Secrets[ServerCredentialKind.JumpPrivateKeyPassphrase] = "jump-passphrase";
        f.JumpProbe(JumpKey);
        f.Target(TargetKey, SshConnectionErrorCode.AuthenticationFailed);
        f.Target(TargetKey, SshConnectionErrorCode.None);

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.True(result.IsSuccess);
        var login = f.Tunnels.Created[0].JumpLogin;
        Assert.Equal(SshLoginKind.PrivateKey, login.Kind);
        Assert.Equal("C:\\keys\\jump", login.PrivateKeyPath);
        Assert.Equal("jump-passphrase", login.Passphrase);
        Assert.Equal(
            [ServerCredentialKind.JumpPrivateKeyPassphrase, ServerCredentialKind.Password],
            f.Credentials.Reads.Select(r => r.Kind));
        Assert.All(f.Credentials.Reads.Take(1), r => Assert.Equal(Fixture.JumpReference, r.ReferenceId));
    }

    [Fact]
    public async Task Missing_jump_credential_is_a_jump_credential_error_and_no_tunnel_is_built()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.Credentials.Secrets.Remove(ServerCredentialKind.JumpPassword);
        f.JumpProbe(JumpKey);

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(SshConnectionErrorCode.JumpCredentialUnavailable, result.ErrorCode);
        Assert.Empty(f.Tunnels.Created);
    }

    [Theory]
    [InlineData((int)JumpTunnelOpenStage.TunnelListen, SshConnectionErrorCode.None, SshConnectionErrorCode.LocalTunnelFailed)]
    [InlineData((int)JumpTunnelOpenStage.Jump, SshConnectionErrorCode.AuthenticationFailed, SshConnectionErrorCode.JumpAuthenticationFailed)]
    [InlineData((int)JumpTunnelOpenStage.Jump, SshConnectionErrorCode.ConnectionRefused, SshConnectionErrorCode.JumpConnectionFailed)]
    public async Task Tunnel_open_failures_never_dial_the_target_and_always_tear_down(
        int stageValue,
        SshConnectionErrorCode jumpCode,
        SshConnectionErrorCode expected)
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.JumpProbe(JumpKey);
        var stage = (JumpTunnelOpenStage)stageValue;
        f.Tunnels.OpenResult = new JumpTunnelOpenResult(stage, new SshSessionResult { ErrorCode = jumpCode, IdentificationReceived = true });

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(expected, result.ErrorCode);
        var tunnel = Assert.Single(f.Tunnels.Created);
        Assert.Empty(tunnel.TargetSessions);
        Assert.True(tunnel.Disposed);
        Assert.Empty(f.Routed.Lookups);
    }

    [Theory]
    [InlineData(true, SshConnectionErrorCode.TargetUnreachableViaJump)]
    [InlineData(false, SshConnectionErrorCode.JumpConnectionFailed)]
    public async Task Target_that_never_identifies_is_classified_structurally(bool jumpConnected, SshConnectionErrorCode expected)
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.JumpProbe(JumpKey);
        f.Tunnels.EnqueueTarget(new Session(null, SshConnectionErrorCode.None, identificationReceived: false));
        f.OnTunnelCreated = tunnel => tunnel.JumpConnected = jumpConnected;

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(expected, result.ErrorCode);
        Assert.DoesNotContain(f.Credentials.Reads, r => r.Kind == ServerCredentialKind.Password);
        Assert.True(f.Tunnels.Created[0].Disposed);
    }

    [Fact]
    public async Task Target_authentication_failure_is_the_targets()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.JumpProbe(JumpKey);
        f.Target(TargetKey, SshConnectionErrorCode.AuthenticationFailed);
        f.Target(TargetKey, SshConnectionErrorCode.AuthenticationFailed);

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(SshConnectionErrorCode.AuthenticationFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Jump_dropping_mid_session_is_a_jump_failure()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.JumpProbe(JumpKey);
        f.Target(TargetKey, SshConnectionErrorCode.AuthenticationFailed);
        var session = new Session(TargetKey, SshConnectionErrorCode.RemoteDisconnected);
        f.Tunnels.EnqueueTarget(session);
        session.DuringOperation = () => f.Tunnels.Created[0].JumpConnected = false;

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(SshConnectionErrorCode.JumpConnectionFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Missing_target_credential_keeps_its_own_code_through_a_jump()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.Credentials.Secrets.Remove(ServerCredentialKind.Password);
        f.JumpProbe(JumpKey);
        f.Target(TargetKey, SshConnectionErrorCode.AuthenticationFailed);

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(SshConnectionErrorCode.CredentialUnavailable, result.ErrorCode);
        Assert.True(f.Tunnels.Created[0].Disposed);
    }

    [Fact]
    public async Task Caller_cancellation_during_the_target_is_cancelled_and_tears_down()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.JumpProbe(JumpKey);
        f.Tunnels.EnqueueTarget(new Session(TargetKey, SshConnectionErrorCode.None, waitUntilCancelled: true));
        using var cancellation = new CancellationTokenSource();
        var operation = f.Service.TestConnectionAsync(f.Request(), cancellation.Token);

        // Bounded: if the pipeline never reaches the target (a regression), fall through and fail the asserts
        // instead of hanging the run.
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while ((f.Tunnels.Created.Count == 0 || f.Tunnels.Created[0].TargetSessions.Count == 0)
               && !operation.IsCompleted
               && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(5);
        }

        Assert.Single(Assert.Single(f.Tunnels.Created).TargetSessions);

        await cancellation.CancelAsync();
        var result = await operation;

        Assert.Equal(SshConnectionErrorCode.Cancelled, result.ErrorCode);
        Assert.True(f.Tunnels.Created[0].Disposed);
    }

    [Fact]
    public async Task Deadline_during_the_target_is_a_timeout_and_tears_down()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.JumpProbe(JumpKey);
        f.Tunnels.EnqueueTarget(new Session(TargetKey, SshConnectionErrorCode.None, waitUntilCancelled: true));

        var result = await f.Service.TestConnectionAsync(f.Request(timeout: TimeSpan.FromMilliseconds(200)));

        Assert.Equal(SshConnectionErrorCode.ConnectionTimedOut, result.ErrorCode);
        Assert.True(f.Tunnels.Created[0].Disposed);
    }

    [Fact]
    public async Task Deadline_during_the_jump_is_a_timeout_not_a_jump_failure()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.Sessions.Probes.Enqueue(new Session(JumpKey, SshConnectionErrorCode.None, waitUntilCancelled: true) { Name = "jump.probe" });

        var result = await f.Service.TestConnectionAsync(f.Request(timeout: TimeSpan.FromMilliseconds(200)));

        Assert.Equal(SshConnectionErrorCode.ConnectionTimedOut, result.ErrorCode);
        Assert.Empty(f.Credentials.Reads);
        Assert.Empty(f.Tunnels.Created);
    }

    [Fact]
    public async Task Caller_cancellation_during_the_jump_stays_cancelled()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.Sessions.Probes.Enqueue(new Session(JumpKey, SshConnectionErrorCode.None, waitUntilCancelled: true) { Name = "jump.probe" });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var result = await f.Service.TestConnectionAsync(f.Request(timeout: TimeSpan.FromSeconds(30)), cancellation.Token);

        Assert.Equal(SshConnectionErrorCode.Cancelled, result.ErrorCode);
    }

    [Fact]
    public async Task Deadline_during_the_tunnel_open_is_a_timeout()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.JumpProbe(JumpKey);
        f.Tunnels.OpenDelay = Timeout.InfiniteTimeSpan;

        var result = await f.Service.TestConnectionAsync(f.Request(timeout: TimeSpan.FromMilliseconds(200)));

        Assert.Equal(SshConnectionErrorCode.ConnectionTimedOut, result.ErrorCode);
        Assert.True(Assert.Single(f.Tunnels.Created).Disposed);
    }

    [Fact]
    public async Task A_jump_error_raised_before_the_target_failure_is_classified_is_a_jump_failure_on_the_real_tunnel()
    {
        // The REAL SshJumpTunnel owner (fake SSH.NET parts): the jump reports ErrorOccurred during the target
        // operation while its IsConnected still reads true. The latch must win over the lagging flag.
        var parts = new RealTunnelParts();
        var f = new Fixture(tunnelFactory: parts).TrustJump().TrustTarget();
        f.JumpProbe(JumpKey);
        parts.Targets.Enqueue(new Session(TargetKey, SshConnectionErrorCode.AuthenticationFailed));
        var dropping = new Session(TargetKey, SshConnectionErrorCode.RemoteDisconnected);
        dropping.DuringOperation = () => parts.Jump!.RaiseFault();
        parts.Targets.Enqueue(dropping);

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(SshConnectionErrorCode.JumpConnectionFailed, result.ErrorCode);
        Assert.True(parts.Jump!.IsConnected);
        Assert.True(parts.Jump.Disposed);
    }

    [Fact]
    public async Task Without_a_jump_error_the_same_target_failure_stays_the_targets_on_the_real_tunnel()
    {
        var parts = new RealTunnelParts();
        var f = new Fixture(tunnelFactory: parts).TrustJump().TrustTarget();
        f.JumpProbe(JumpKey);
        parts.Targets.Enqueue(new Session(TargetKey, SshConnectionErrorCode.AuthenticationFailed));
        parts.Targets.Enqueue(new Session(TargetKey, SshConnectionErrorCode.RemoteDisconnected));

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(SshConnectionErrorCode.RemoteDisconnected, result.ErrorCode);
    }

    [Fact]
    public async Task Jump_private_key_that_cannot_be_loaded_is_a_jump_credential_error()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.JumpProbe(JumpKey);
        f.Tunnels.CreateException = new SshPrivateKeyLoadException(new InvalidOperationException());

        var result = await f.Service.TestConnectionAsync(f.Request());

        Assert.Equal(SshConnectionErrorCode.JumpCredentialUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task Logs_name_the_logical_target_never_secrets()
    {
        var f = new Fixture().TrustJump().TrustTarget();
        f.JumpProbe(JumpKey);
        f.Target(TargetKey, SshConnectionErrorCode.AuthenticationFailed);
        f.Target(TargetKey, SshConnectionErrorCode.RemoteDisconnected);

        await f.Service.TestConnectionAsync(f.Request());

        var all = string.Join("\n", f.Logger.Messages);
        Assert.Contains("10.0.0.5", all);
        Assert.DoesNotContain("jump-secret", all);
        Assert.DoesNotContain("target-secret", all);
        Assert.DoesNotContain("127.0.0.1", all);
    }

    private static TrustedRoutedHostKey Routed(SshRoute route, HostKeyIdentity identity) =>
        new() { Route = route, Identity = identity };

    private sealed class Fixture
    {
        public static readonly Guid JumpReference = Guid.Parse("33333333-3333-3333-3333-333333333333");
        private readonly Func<JumpHop, JumpHop>? _configureJump;

        public Fixture(Func<JumpHop, JumpHop>? configureJump = null, IJumpTunnelFactory? tunnelFactory = null)
        {
            _configureJump = configureJump;
            Direct = new DirectTrustStore(Log);
            Routed = new RoutedTrustStore(Log);
            Credentials = new CredentialStore(Log);
            Credentials.Secrets[ServerCredentialKind.JumpPassword] = "jump-secret";
            Credentials.Secrets[ServerCredentialKind.Password] = "target-secret";
            Tunnels = new TunnelFactory(Log);
            Sessions = new ProbeFactory(Log);
            Service = new SshConnectionService(Direct, Routed, Credentials, Logger, Sessions, tunnelFactory ?? new HookedFactory(this));
        }

        public EventLog Log { get; } = new();

        public DirectTrustStore Direct { get; }

        public RoutedTrustStore Routed { get; }

        public CredentialStore Credentials { get; }

        public TunnelFactory Tunnels { get; }

        public ProbeFactory Sessions { get; }

        public ListLogger Logger { get; } = new();

        public SshConnectionService Service { get; }

        public Action<Tunnel>? OnTunnelCreated { get; set; }

        public Fixture TrustJump(HostKeyIdentity? identity = null)
        {
            Direct.Entries[JumpEndpoint] = new TrustedHostKey { Endpoint = JumpEndpoint, Identity = identity ?? JumpKey };
            return this;
        }

        public Fixture TrustTarget(HostKeyIdentity? identity = null)
        {
            Routed.Entries[Route] = SshConnectionServiceRoutedTests.Routed(Route, identity ?? TargetKey);
            return this;
        }

        public void JumpProbe(HostKeyIdentity identity) =>
            Sessions.Probes.Enqueue(new Session(identity, SshConnectionErrorCode.AuthenticationFailed) { Name = "jump.probe", Log = Log });

        public void Target(HostKeyIdentity identity, SshConnectionErrorCode whenTrusted) =>
            Tunnels.EnqueueTarget(new Session(identity, whenTrusted));

        public SshConnectionRequest Request(
            SecretValue? credentialOverride = null,
            SecretValue? jumpOverride = null,
            TimeSpan? timeout = null)
        {
            var jump = new JumpHop
            {
                Host = "bastion.example",
                Port = 2222,
                Username = "jumper",
                AuthenticationMethod = AuthenticationMethod.Password,
                CredentialReferenceId = JumpReference
            };
            return new SshConnectionRequest
            {
                Server = new Server
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Name = "Routed",
                    Host = "10.0.0.5",
                    Port = 22,
                    Username = "tester",
                    AuthenticationMethod = AuthenticationMethod.Password,
                    CredentialReferenceId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Route = new ServerRoute { Jump = _configureJump?.Invoke(jump) ?? jump }
                },
                CredentialOverride = credentialOverride,
                JumpCredentialOverride = jumpOverride,
                Timeout = timeout ?? TimeSpan.FromSeconds(5)
            };
        }

        private sealed class HookedFactory(Fixture owner) : IJumpTunnelFactory
        {
            public IJumpTunnel Create(SshDialTarget jump, SshLogin jumpLogin, SshEndpoint target, TimeSpan timeout)
            {
                var tunnel = (Tunnel)owner.Tunnels.Create(jump, jumpLogin, target, timeout);
                owner.OnTunnelCreated?.Invoke(tunnel);
                return tunnel;
            }
        }
    }

    /// <summary>Builds the PRODUCTION <see cref="SshJumpTunnel"/> over fake SSH.NET parts.</summary>
    private sealed class RealTunnelParts : IJumpTunnelFactory, ISshDialSessionFactory, ITcpOwnerLookup
    {
        public Queue<Session> Targets { get; } = new();

        public FakeJump? Jump { get; private set; }

        public IJumpTunnel Create(SshDialTarget jump, SshLogin jumpLogin, SshEndpoint target, TimeSpan timeout) =>
            new SshJumpTunnel(Jump = new FakeJump(), target, this, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, this, Environment.ProcessId);

        public ISshSession Create(SshDialTarget dial, SshLogin login, TimeSpan timeout, ISshConnectGate? gate) => Targets.Dequeue();

        public int? FindLoopbackConnectionOwner(int localPort, int remotePort) => null;

        public bool IsLoopbackListenerOwnedBy(int port, int processId) => true;

        public sealed class FakeJump : IJumpClient
        {
            public event Action? Faulted;

            public bool IsConnected => true;

            public bool Disposed { get; private set; }

            public void RaiseFault() => Faulted?.Invoke();

            public Task<SshSessionResult> ConnectAsync(Func<HostKeyIdentity, bool> hostKeyVerifier, CancellationToken cancellationToken) =>
                Task.FromResult(new SshSessionResult { ErrorCode = SshConnectionErrorCode.None, IdentificationReceived = true });

            public ILocalForward CreateLocalForward(string bindHost, string targetHost, uint targetPort) => new Forward();

            public void Dispose() => Disposed = true;
        }

        private sealed class Forward : ILocalForward
        {
            public string BoundHost => SshJumpTunnel.LoopbackHost;

            public uint BoundPort { get; private set; }

            public bool IsStarted { get; private set; }

            public void Start(Action<string, uint> onRequest)
            {
                BoundPort = 40000;
                IsStarted = true;
            }

            public void Stop() => IsStarted = false;

            public void Dispose()
            {
            }
        }
    }

    /// <summary>The direct factory: a routed operation may use it ONLY for the jump host-key probe.</summary>
    private sealed class ProbeFactory(EventLog log) : ISshSessionFactory
    {
        public Queue<Session> Probes { get; } = new();

        public ISshSession CreateJumpHostKeyProbe(SshDialTarget jump, TimeSpan timeout) => Probes.Dequeue();

        public ISshSession CreateHostKeyProbe(Server server, TimeSpan timeout) => Forbidden();

        public ISshSession CreatePasswordSession(Server server, string password, TimeSpan timeout) => Forbidden();

        public ISshSession CreatePrivateKeySession(Server server, string privateKeyPath, string? passphrase, TimeSpan timeout) =>
            Forbidden();

        private ISshSession Forbidden()
        {
            log.Add("DIRECT-DIAL");
            throw new InvalidOperationException("A routed server must never be dialled direct.");
        }
    }

    private sealed class ListLogger : ILogger<SshConnectionService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
