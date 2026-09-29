using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Collectors.Workloads;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Tests.SSH;

/// <summary>
/// Doubles for the ProxyJump pipeline tests. They replace only the NETWORK (sessions, tunnel); the service,
/// its ordering and its store/credential selection are the production code under test. Every double writes to
/// a shared <see cref="EventLog"/> so a test can assert the exact order of trust lookups, credential reads and
/// dials.
/// </summary>
internal static class SshRoutedTestDoubles
{
    public sealed class EventLog
    {
        public List<string> Events { get; } = [];

        public void Add(string entry)
        {
            lock (Events)
            {
                Events.Add(entry);
            }
        }
    }

    public sealed class DirectTrustStore(EventLog? log = null) : IHostKeyTrustStore
    {
        public Dictionary<SshEndpoint, TrustedHostKey> Entries { get; } = [];

        public List<SshEndpoint> Lookups { get; } = [];

        public List<(SshEndpoint Endpoint, HostKeyIdentity Identity)> Writes { get; } = [];

        public Task<TrustedHostKey?> GetAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default)
        {
            Lookups.Add(endpoint);
            log?.Add($"direct.get {endpoint}");
            return Task.FromResult(Entries.GetValueOrDefault(endpoint));
        }

        public Task TrustAsync(SshEndpoint endpoint, HostKeyIdentity identity, CancellationToken cancellationToken = default)
        {
            Writes.Add((endpoint, identity));
            log?.Add($"direct.trust {endpoint}");
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    public sealed class RoutedTrustStore(EventLog? log = null) : IRoutedHostKeyTrustStore
    {
        public Dictionary<SshRoute, TrustedRoutedHostKey> Entries { get; } = [];

        public List<SshRoute> Lookups { get; } = [];

        public List<(SshRoute Route, HostKeyIdentity Identity)> Writes { get; } = [];

        public Task<TrustedRoutedHostKey?> GetAsync(SshRoute route, CancellationToken cancellationToken = default)
        {
            Lookups.Add(route);
            log?.Add($"routed.get {route}");
            return Task.FromResult(Entries.GetValueOrDefault(route));
        }

        public Task TrustAsync(SshRoute route, HostKeyIdentity identity, CancellationToken cancellationToken = default)
        {
            Writes.Add((route, identity));
            log?.Add($"routed.trust {route}");
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(SshRoute route, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    public sealed class CredentialStore(EventLog? log = null) : IServerCredentialStore
    {
        public Dictionary<ServerCredentialKind, string> Secrets { get; } = [];

        public List<CredentialReference> Reads { get; } = [];

        public Task WriteAsync(CredentialReference reference, SecretValue secret, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SecretValue?> ReadAsync(CredentialReference reference, CancellationToken cancellationToken = default)
        {
            Reads.Add(reference);
            log?.Add($"cred.read {reference.Kind}");
            return Task.FromResult(Secrets.TryGetValue(reference.Kind, out var secret) ? new SecretValue(secret) : null);
        }

        public Task<bool> DeleteAsync(CredentialReference reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    /// <summary>A scripted session: presents <c>identity</c>; returns <c>whenTrusted</c> if the verifier accepts it.</summary>
    public sealed class Session(
        HostKeyIdentity? identity,
        SshConnectionErrorCode whenTrusted,
        bool identificationReceived = true,
        bool waitUntilCancelled = false) : ISshSession
    {
        public string Name { get; set; } = "session";

        public EventLog? Log { get; set; }

        public bool Disposed { get; private set; }

        public List<bool> VerifierAnswers { get; } = [];

        /// <summary>Runs just before the result is produced (e.g. to drop the jump mid-session).</summary>
        public Action? DuringOperation { get; set; }

        public Task<SshSessionResult> ConnectAsync(Func<HostKeyIdentity, bool> hostKeyVerifier, CancellationToken cancellationToken) =>
            RunAsync(hostKeyVerifier, cancellationToken);

        public Task<SshSessionResult> DetectOperatingSystemAsync(Func<HostKeyIdentity, bool> hostKeyVerifier, CancellationToken cancellationToken) =>
            RunAsync(hostKeyVerifier, cancellationToken);

        public Task<SshSessionResult> CollectLinuxMetricsAsync(Func<HostKeyIdentity, bool> hostKeyVerifier, TimeSpan cpuSampleInterval, CancellationToken cancellationToken) =>
            RunAsync(hostKeyVerifier, cancellationToken);

        public Task<SshSessionResult> CollectMacOsMetricsAsync(Func<HostKeyIdentity, bool> hostKeyVerifier, CancellationToken cancellationToken) =>
            RunAsync(hostKeyVerifier, cancellationToken);

        public Task<SshSessionResult> CollectWorkloadsAsync(Func<HostKeyIdentity, bool> hostKeyVerifier, WorkloadCollectionPlan plan, CancellationToken cancellationToken) =>
            RunAsync(hostKeyVerifier, cancellationToken);

        public void Dispose() => Disposed = true;

        private async Task<SshSessionResult> RunAsync(Func<HostKeyIdentity, bool> verifier, CancellationToken cancellationToken)
        {
            Log?.Add($"{Name}.connect");
            if (waitUntilCancelled)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return new SshSessionResult { ErrorCode = SshConnectionErrorCode.Cancelled };
                }
            }

            if (!identificationReceived)
            {
                return new SshSessionResult { ErrorCode = SshConnectionErrorCode.RemoteDisconnected, IdentificationReceived = false };
            }

            var trusted = identity is not null && verifier(identity);
            VerifierAnswers.Add(trusted);
            DuringOperation?.Invoke();
            return new SshSessionResult
            {
                ErrorCode = trusted ? whenTrusted : SshConnectionErrorCode.HostKeyMismatch,
                HostKeyRejected = !trusted,
                PresentedHostKey = identity,
                IdentificationReceived = true
            };
        }
    }

    public sealed class Tunnel(TunnelFactory owner, SshDialTarget jump, SshLogin jumpLogin, SshEndpoint target) : IJumpTunnel
    {
        public SshDialTarget JumpDial { get; } = jump;

        public SshLogin JumpLogin { get; } = jumpLogin;

        public SshEndpoint Target { get; } = target;

        public bool Disposed { get; private set; }

        public int DisposeCount { get; private set; }

        public List<(string Username, SshLogin Login)> TargetSessions { get; } = [];

        public bool JumpConnected { get; set; } = true;

        public bool IsJumpConnected => JumpConnected;

        public Task<JumpTunnelOpenResult> OpenAsync(Func<HostKeyIdentity, bool> jumpHostKeyVerifier, CancellationToken cancellationToken)
        {
            owner.Log?.Add("tunnel.open");
            if (owner.OpenResult is { } scripted)
            {
                return Task.FromResult(scripted);
            }

            var accepted = jumpHostKeyVerifier(owner.JumpKey);
            return Task.FromResult(accepted
                ? new JumpTunnelOpenResult(JumpTunnelOpenStage.Opened, new SshSessionResult { ErrorCode = SshConnectionErrorCode.None, IdentificationReceived = true })
                : new JumpTunnelOpenResult(
                    JumpTunnelOpenStage.Jump,
                    new SshSessionResult { ErrorCode = SshConnectionErrorCode.HostKeyMismatch, HostKeyRejected = true, IdentificationReceived = true }));
        }

        public ISshSession CreateTargetSession(string username, SshLogin login, TimeSpan timeout)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            TargetSessions.Add((username, login));
            var session = owner.Targets.Dequeue();
            session.Log ??= owner.Log;
            session.Name = login.Kind == SshLoginKind.None ? "target.probe" : $"target.{login.Kind}";
            return session;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (!Disposed)
            {
                owner.Log?.Add("tunnel.dispose");
            }

            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    public sealed class TunnelFactory(EventLog? log = null) : IJumpTunnelFactory
    {
        public EventLog? Log { get; } = log;

        public Queue<Session> Targets { get; } = new();

        public List<Tunnel> Created { get; } = [];

        public JumpTunnelOpenResult? OpenResult { get; set; }

        /// <summary>The key the jump presents on the authenticated connection (defaults to the probe's).</summary>
        public HostKeyIdentity JumpKey { get; set; } = Key(1);

        public Exception? CreateException { get; set; }

        public void EnqueueTarget(Session session) => Targets.Enqueue(session);

        public IJumpTunnel Create(SshDialTarget jump, SshLogin jumpLogin, SshEndpoint target, TimeSpan timeout)
        {
            Log?.Add($"tunnel.create {jumpLogin.Kind}");
            if (CreateException is not null)
            {
                throw CreateException;
            }

            var tunnel = new Tunnel(this, jump, jumpLogin, target);
            Created.Add(tunnel);
            return tunnel;
        }
    }

    public static HostKeyIdentity Key(byte value) => HostKeyIdentity.Create(
        "ssh-ed25519",
        Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray()));
}
