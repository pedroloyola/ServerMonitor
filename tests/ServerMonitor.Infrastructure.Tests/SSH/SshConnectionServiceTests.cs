using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Security;
using ServerMonitor.Infrastructure.Collectors.Linux;
using ServerMonitor.Infrastructure.Collectors.MacOS;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.Infrastructure.Tests.SSH;

public sealed class SshConnectionServiceTests
{
    [Fact]
    public async Task Unknown_host_is_returned_without_loading_credentials()
    {
        var fixture = new Fixture();
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.HostKeyMismatch));

        var result = await fixture.Service.TestConnectionAsync(Request());

        Assert.Equal(ServerConnectionState.HostKeyUnknown, result.State);
        Assert.Equal(SshConnectionErrorCode.HostKeyUnknown, result.ErrorCode);
        Assert.Equal(Identity(1), result.PresentedHostKey);
        Assert.Equal(0, fixture.Credentials.ReadCount);
        Assert.Equal(1, fixture.Factory.ProbeCount);
        Assert.Equal(0, fixture.Factory.AuthenticatedCount);
    }

    [Fact]
    public async Task Changed_host_key_is_blocked_before_credentials_are_loaded()
    {
        var fixture = new Fixture
        {
            TrustedHostKey = Trusted(Identity(1))
        };
        fixture.Factory.Enqueue(new FakeSession(Identity(2), SshConnectionErrorCode.HostKeyMismatch));

        var result = await fixture.Service.ConnectAsync(Request());

        Assert.Equal(ServerConnectionState.HostKeyMismatch, result.State);
        Assert.Equal(SshConnectionErrorCode.HostKeyMismatch, result.ErrorCode);
        Assert.Equal(0, fixture.Credentials.ReadCount);
        Assert.Equal(0, fixture.Factory.AuthenticatedCount);
    }

    [Fact]
    public async Task Matching_host_key_loads_password_only_after_probe()
    {
        var fixture = new Fixture
        {
            TrustedHostKey = Trusted(Identity(1))
        };
        fixture.Credentials.Secret = "correct horse battery staple";
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.AuthenticationFailed));
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.None));

        var result = await fixture.Service.ConnectAsync(Request());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, fixture.Credentials.ReadCount);
        Assert.Equal(ServerCredentialKind.Password, fixture.Credentials.LastReference?.Kind);
        Assert.Equal("correct horse battery staple", fixture.Factory.Password);
        Assert.Equal(new[] { "probe", "password" }, fixture.Factory.Calls);
    }

    [Fact]
    public async Task Key_change_between_probe_and_authentication_is_blocked()
    {
        var fixture = new Fixture
        {
            TrustedHostKey = Trusted(Identity(1))
        };
        fixture.Credentials.Secret = "secret";
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.AuthenticationFailed));
        fixture.Factory.Enqueue(new FakeSession(Identity(2), SshConnectionErrorCode.HostKeyMismatch));

        var result = await fixture.Service.ConnectAsync(Request());

        Assert.Equal(ServerConnectionState.HostKeyMismatch, result.State);
        Assert.Equal(SshConnectionErrorCode.HostKeyMismatch, result.ErrorCode);
    }

    [Fact]
    public async Task Unencrypted_private_key_does_not_require_a_secret()
    {
        var keyPath = Path.GetTempFileName();
        try
        {
            var fixture = new Fixture
            {
                TrustedHostKey = Trusted(Identity(1))
            };
            fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.AuthenticationFailed));
            fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.None));

            var request = Request(server => server with
            {
                AuthenticationMethod = AuthenticationMethod.SshKey,
                PrivateKeyPath = keyPath,
                CredentialReferenceId = null
            });

            var result = await fixture.Service.ConnectAsync(request);

            Assert.True(result.IsSuccess);
            Assert.Equal(0, fixture.Credentials.ReadCount);
            Assert.Equal(keyPath, fixture.Factory.PrivateKeyPath);
            Assert.Null(fixture.Factory.Passphrase);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task Private_key_passphrase_uses_its_credential_kind()
    {
        var keyPath = Path.GetTempFileName();
        try
        {
            var fixture = new Fixture
            {
                TrustedHostKey = Trusted(Identity(1))
            };
            fixture.Credentials.Secret = "key passphrase";
            fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.AuthenticationFailed));
            fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.None));

            var result = await fixture.Service.ConnectAsync(Request(server => server with
            {
                AuthenticationMethod = AuthenticationMethod.SshKey,
                PrivateKeyPath = keyPath
            }));

            Assert.True(result.IsSuccess);
            Assert.Equal(ServerCredentialKind.PrivateKeyPassphrase, fixture.Credentials.LastReference?.Kind);
            Assert.Equal("key passphrase", fixture.Factory.Passphrase);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task Detect_operating_system_uses_the_dedicated_session_operation()
    {
        var fixture = new Fixture
        {
            TrustedHostKey = Trusted(Identity(1))
        };
        fixture.Credentials.Secret = "password";
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.AuthenticationFailed));
        fixture.Factory.Enqueue(new FakeSession(
            Identity(1),
            SshConnectionErrorCode.None,
            ServerOperatingSystem.Linux));

        var result = await fixture.Service.DetectOperatingSystemAsync(Request());

        Assert.True(result.IsSuccess);
        Assert.Equal(ServerOperatingSystem.Linux, result.DetectedOperatingSystem);
        Assert.True(fixture.Factory.LastSession!.DetectWasCalled);
    }

    [Fact]
    public async Task Invalid_request_does_not_create_a_session()
    {
        var fixture = new Fixture();
        var request = Request(server => server with { Host = " " });

        var result = await fixture.Service.ConnectAsync(request);

        Assert.Equal(SshConnectionErrorCode.InvalidConfiguration, result.ErrorCode);
        Assert.Empty(fixture.Factory.Calls);
        Assert.Equal(0, fixture.Credentials.ReadCount);
    }

    [Fact]
    public async Task Excessive_timeout_is_invalid_instead_of_throwing()
    {
        var fixture = new Fixture();

        var result = await fixture.Service.ConnectAsync(Request(timeout: TimeSpan.FromDays(60)));

        Assert.Equal(SshConnectionErrorCode.InvalidConfiguration, result.ErrorCode);
        Assert.Empty(fixture.Factory.Calls);
    }

    [Fact]
    public async Task Missing_password_reference_is_reported_after_host_verification()
    {
        var fixture = new Fixture
        {
            TrustedHostKey = Trusted(Identity(1))
        };
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.AuthenticationFailed));

        var result = await fixture.Service.ConnectAsync(Request(server => server with
        {
            CredentialReferenceId = null
        }));

        Assert.Equal(SshConnectionErrorCode.CredentialNotConfigured, result.ErrorCode);
        Assert.Equal(0, fixture.Credentials.ReadCount);
        Assert.Equal(1, fixture.Factory.ProbeCount);
    }

    [Fact]
    public async Task Missing_private_key_is_reported_without_creating_authenticated_session()
    {
        var fixture = new Fixture
        {
            TrustedHostKey = Trusted(Identity(1))
        };
        fixture.Factory.PrivateKeyException = new FileNotFoundException();
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.AuthenticationFailed));

        var result = await fixture.Service.ConnectAsync(Request(server => server with
        {
            AuthenticationMethod = AuthenticationMethod.SshKey,
            PrivateKeyPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"),
            CredentialReferenceId = null
        }));

        Assert.Equal(SshConnectionErrorCode.PrivateKeyUnavailable, result.ErrorCode);
        Assert.Equal(0, fixture.Factory.AuthenticatedCount);
        Assert.Equal(0, fixture.Credentials.ReadCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_distinct_from_timeout()
    {
        var fixture = new Fixture();
        fixture.Factory.Enqueue(FakeSession.WaitUntilCancelled());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await fixture.Service.ConnectAsync(Request(), cancellation.Token);

        Assert.Equal(SshConnectionErrorCode.Cancelled, result.ErrorCode);
        Assert.Equal(ServerConnectionState.Cancelled, result.State);
    }

    [Fact]
    public async Task Linked_deadline_maps_to_timeout()
    {
        var fixture = new Fixture();
        fixture.Factory.Enqueue(FakeSession.WaitUntilCancelled());

        var result = await fixture.Service.ConnectAsync(Request(timeout: TimeSpan.FromMilliseconds(25)));

        Assert.Equal(SshConnectionErrorCode.ConnectionTimedOut, result.ErrorCode);
        Assert.Equal(ServerConnectionState.TimedOut, result.State);
    }

    [Fact]
    public async Task Logs_never_include_password_or_private_key_path()
    {
        var fixture = new Fixture
        {
            TrustedHostKey = Trusted(Identity(1))
        };
        fixture.Credentials.Secret = "never-log-this-password";
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.AuthenticationFailed));
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.AuthenticationFailed));

        await fixture.Service.ConnectAsync(Request());

        var log = string.Join(Environment.NewLine, fixture.Logger.Messages);
        Assert.DoesNotContain("never-log-this-password", log, StringComparison.Ordinal);
        Assert.DoesNotContain(".ssh", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Linux_metrics_reuses_trust_then_credentials_and_one_authenticated_session()
    {
        var raw = new LinuxMetricsRawData
        {
            FirstCpuStat = "cpu 1 2 3 4",
            SecondCpuStat = "cpu 2 3 4 5",
            Hostname = "ubuntu"
        };
        var fixture = new Fixture
        {
            TrustedHostKey = Trusted(Identity(1))
        };
        fixture.Credentials.Secret = "password";
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.AuthenticationFailed));
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.None, linuxMetrics: raw));

        var result = await fixture.Service.CollectAsync(
            Request().Server with { OperatingSystem = ServerOperatingSystem.Linux },
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(2));

        Assert.True(result.IsSuccess);
        Assert.Same(raw, result.Data);
        Assert.True(fixture.Factory.LastSession!.CollectWasCalled);
        Assert.Equal(new[] { "probe", "password" }, fixture.Factory.Calls);
        Assert.Equal(1, fixture.Credentials.ReadCount);
    }

    [Fact]
    public async Task Linux_metrics_with_no_remote_sources_is_not_marked_successful()
    {
        var fixture = new Fixture
        {
            TrustedHostKey = Trusted(Identity(1))
        };
        fixture.Credentials.Secret = "password";
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.AuthenticationFailed));
        fixture.Factory.Enqueue(new FakeSession(
            Identity(1),
            SshConnectionErrorCode.None,
            linuxMetrics: new LinuxMetricsRawData()));

        var result = await fixture.Service.CollectAsync(
            Request().Server with { OperatingSystem = ServerOperatingSystem.Linux },
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(2));

        Assert.False(result.IsSuccess);
        Assert.True(result.ConnectionResult.IsSuccess);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public async Task Linux_metrics_unknown_host_never_loads_credentials_or_collects()
    {
        var fixture = new Fixture();
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.HostKeyMismatch));

        var result = await fixture.Service.CollectAsync(
            Request().Server with { OperatingSystem = ServerOperatingSystem.Linux },
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(2));

        Assert.False(result.IsSuccess);
        Assert.Equal(SshConnectionErrorCode.HostKeyUnknown, result.ConnectionResult.ErrorCode);
        Assert.Null(result.Data);
        Assert.Equal(0, fixture.Credentials.ReadCount);
        Assert.Equal(0, fixture.Factory.AuthenticatedCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5001)]
    public async Task Linux_metrics_rejects_invalid_sample_interval(int milliseconds)
    {
        var fixture = new Fixture();

        var result = await fixture.Service.CollectAsync(
            Request().Server,
            TimeSpan.FromMilliseconds(milliseconds),
            TimeSpan.FromSeconds(2));

        Assert.Equal(SshConnectionErrorCode.InvalidConfiguration, result.ConnectionResult.ErrorCode);
        Assert.Empty(fixture.Factory.Calls);
    }

    [Fact]
    public async Task MacOs_metrics_reuses_trust_then_credentials_and_one_authenticated_session()
    {
        var raw = new MacOsMetricsRawData
        {
            CpuTop = "CPU usage: 3.00% user, 5.00% sys, 92.00% idle",
            Hostname = "mac-mini"
        };
        var fixture = new Fixture
        {
            TrustedHostKey = Trusted(Identity(1))
        };
        fixture.Credentials.Secret = "password";
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.AuthenticationFailed));
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.None, macOsMetrics: raw));

        var result = await fixture.Service.CollectAsync(
            Request().Server with { OperatingSystem = ServerOperatingSystem.MacOS },
            TimeSpan.FromSeconds(2));

        Assert.True(result.IsSuccess);
        Assert.Same(raw, result.Data);
        Assert.True(fixture.Factory.LastSession!.CollectMacOsWasCalled);
        Assert.Equal(new[] { "probe", "password" }, fixture.Factory.Calls);
        Assert.Equal(1, fixture.Credentials.ReadCount);
    }

    [Fact]
    public async Task MacOs_metrics_with_no_remote_sources_is_not_marked_successful()
    {
        var fixture = new Fixture
        {
            TrustedHostKey = Trusted(Identity(1))
        };
        fixture.Credentials.Secret = "password";
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.AuthenticationFailed));
        fixture.Factory.Enqueue(new FakeSession(
            Identity(1),
            SshConnectionErrorCode.None,
            macOsMetrics: new MacOsMetricsRawData()));

        var result = await fixture.Service.CollectAsync(
            Request().Server with { OperatingSystem = ServerOperatingSystem.MacOS },
            TimeSpan.FromSeconds(2));

        Assert.False(result.IsSuccess);
        Assert.True(result.ConnectionResult.IsSuccess);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public async Task MacOs_metrics_unknown_host_never_loads_credentials_or_collects()
    {
        var fixture = new Fixture();
        fixture.Factory.Enqueue(new FakeSession(Identity(1), SshConnectionErrorCode.HostKeyMismatch));

        var result = await fixture.Service.CollectAsync(
            Request().Server with { OperatingSystem = ServerOperatingSystem.MacOS },
            TimeSpan.FromSeconds(2));

        Assert.False(result.IsSuccess);
        Assert.Equal(SshConnectionErrorCode.HostKeyUnknown, result.ConnectionResult.ErrorCode);
        Assert.Null(result.Data);
        Assert.Equal(0, fixture.Credentials.ReadCount);
        Assert.Equal(0, fixture.Factory.AuthenticatedCount);
    }

    private static SshConnectionRequest Request(
        Func<Server, Server>? configure = null,
        TimeSpan? timeout = null)
    {
        var server = new Server
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Test server",
            Host = "server.example",
            Port = 22,
            Username = "tester",
            AuthenticationMethod = AuthenticationMethod.Password,
            CredentialReferenceId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            CreatedAt = DateTimeOffset.UtcNow
        };

        return new SshConnectionRequest
        {
            Server = configure?.Invoke(server) ?? server,
            Timeout = timeout ?? TimeSpan.FromSeconds(2)
        };
    }

    private static HostKeyIdentity Identity(byte value) => HostKeyIdentity.Create(
        "ssh-ed25519",
        Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray()));

    private static TrustedHostKey Trusted(HostKeyIdentity identity) => new()
    {
        Endpoint = SshEndpoint.Create("server.example", 22),
        Identity = identity,
        ConfirmedAt = DateTimeOffset.UtcNow
    };

    private sealed class Fixture
    {
        private readonly FakeTrustStore _trust = new();

        public Fixture()
        {
            Service = new SshConnectionService(_trust, Credentials, Logger, Factory);
        }

        public FakeCredentialStore Credentials { get; } = new();

        public FakeSessionFactory Factory { get; } = new();

        public CollectingLogger<SshConnectionService> Logger { get; } = new();

        public SshConnectionService Service { get; }

        public TrustedHostKey? TrustedHostKey
        {
            set => _trust.TrustedHostKey = value;
        }
    }

    private sealed class FakeTrustStore : IHostKeyTrustStore
    {
        public TrustedHostKey? TrustedHostKey { get; set; }

        public Task<TrustedHostKey?> GetAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult(TrustedHostKey);

        public Task TrustAsync(
            SshEndpoint endpoint,
            HostKeyIdentity identity,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> RemoveAsync(SshEndpoint endpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeCredentialStore : IServerCredentialStore
    {
        public int ReadCount { get; private set; }

        public string? Secret { get; set; }

        public CredentialReference? LastReference { get; private set; }

        public Task WriteAsync(
            CredentialReference reference,
            SecretValue secret,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SecretValue?> ReadAsync(
            CredentialReference reference,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            LastReference = reference;
            return Task.FromResult(Secret is null ? null : new SecretValue(Secret));
        }

        public Task<bool> DeleteAsync(
            CredentialReference reference,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FakeSessionFactory : ISshSessionFactory
    {
        private readonly Queue<FakeSession> _sessions = new();

        public List<string> Calls { get; } = [];

        public int ProbeCount => Calls.Count(call => call == "probe");

        public int AuthenticatedCount => Calls.Count - ProbeCount;

        public string? Password { get; private set; }

        public string? PrivateKeyPath { get; private set; }

        public string? Passphrase { get; private set; }

        public Exception? PrivateKeyException { get; set; }

        public FakeSession? LastSession { get; private set; }

        public void Enqueue(FakeSession session) => _sessions.Enqueue(session);

        public ISshSession CreateHostKeyProbe(Server server, TimeSpan timeout)
        {
            Calls.Add("probe");
            return Take();
        }

        public ISshSession CreatePasswordSession(Server server, string password, TimeSpan timeout)
        {
            Calls.Add("password");
            Password = password;
            return Take();
        }

        public ISshSession CreatePrivateKeySession(
            Server server,
            string privateKeyPath,
            string? passphrase,
            TimeSpan timeout)
        {
            if (PrivateKeyException is not null)
            {
                throw PrivateKeyException;
            }

            Calls.Add("private-key");
            PrivateKeyPath = privateKeyPath;
            Passphrase = passphrase;
            return Take();
        }

        private FakeSession Take() => LastSession = _sessions.Dequeue();
    }

    private sealed class FakeSession : ISshSession
    {
        private readonly HostKeyIdentity? _identity;
        private readonly SshConnectionErrorCode _whenTrusted;
        private readonly ServerOperatingSystem _operatingSystem;
        private readonly bool _waitUntilCancelled;

        public FakeSession(
            HostKeyIdentity? identity,
            SshConnectionErrorCode whenTrusted,
            ServerOperatingSystem operatingSystem = ServerOperatingSystem.Unknown,
            LinuxMetricsRawData? linuxMetrics = null,
            MacOsMetricsRawData? macOsMetrics = null)
        {
            _identity = identity;
            _whenTrusted = whenTrusted;
            _operatingSystem = operatingSystem;
            LinuxMetrics = linuxMetrics;
            MacOsMetrics = macOsMetrics;
        }

        private FakeSession(bool waitUntilCancelled)
        {
            _waitUntilCancelled = waitUntilCancelled;
        }

        public bool DetectWasCalled { get; private set; }

        public bool CollectWasCalled { get; private set; }

        public bool CollectMacOsWasCalled { get; private set; }

        public LinuxMetricsRawData? LinuxMetrics { get; }

        public MacOsMetricsRawData? MacOsMetrics { get; }

        public static FakeSession WaitUntilCancelled() => new(waitUntilCancelled: true);

        public Task<SshSessionResult> ConnectAsync(
            Func<HostKeyIdentity, bool> hostKeyVerifier,
            CancellationToken cancellationToken) => RunAsync(hostKeyVerifier, cancellationToken);

        public async Task<SshSessionResult> DetectOperatingSystemAsync(
            Func<HostKeyIdentity, bool> hostKeyVerifier,
            CancellationToken cancellationToken)
        {
            DetectWasCalled = true;
            return await RunAsync(hostKeyVerifier, cancellationToken);
        }

        public Task<SshSessionResult> CollectLinuxMetricsAsync(
            Func<HostKeyIdentity, bool> hostKeyVerifier,
            TimeSpan cpuSampleInterval,
            CancellationToken cancellationToken)
        {
            CollectWasCalled = true;
            return RunAsync(hostKeyVerifier, cancellationToken);
        }

        public Task<SshSessionResult> CollectMacOsMetricsAsync(
            Func<HostKeyIdentity, bool> hostKeyVerifier,
            CancellationToken cancellationToken)
        {
            CollectMacOsWasCalled = true;
            return RunAsync(hostKeyVerifier, cancellationToken);
        }

        public void Dispose()
        {
        }

        private async Task<SshSessionResult> RunAsync(
            Func<HostKeyIdentity, bool> hostKeyVerifier,
            CancellationToken cancellationToken)
        {
            if (_waitUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            var trusted = _identity is not null && hostKeyVerifier(_identity);
            return new SshSessionResult
            {
                ErrorCode = trusted ? _whenTrusted : SshConnectionErrorCode.HostKeyMismatch,
                PresentedHostKey = _identity,
                DetectedOperatingSystem = _operatingSystem,
                LinuxMetrics = LinuxMetrics,
                MacOsMetrics = MacOsMetrics
            };
        }
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
