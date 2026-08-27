using ServerMonitor.Collectors.Tests.Linux.Fakes;
using ServerMonitor.Collectors.Workloads;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Workloads;
using ServerMonitor.Infrastructure.Collectors.Workloads;

namespace ServerMonitor.Collectors.Tests.Workloads;

public sealed class WorkloadCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Null_server_throws()
    {
        var collector = new WorkloadCollector(new FakeWorkloadRemoteSource(Failure()));

        await Assert.ThrowsAsync<ArgumentNullException>(() => collector.CollectAsync(null!));
    }

    [Fact]
    public async Task Ssh_failure_yields_both_unknown_but_fresh()
    {
        var collector = new WorkloadCollector(new FakeWorkloadRemoteSource(Failure()), new FixedTimeProvider(Now));

        var snapshot = await collector.CollectAsync(Server(ServerOperatingSystem.Linux));

        Assert.Equal(DockerAvailability.Unknown, snapshot.Docker.Availability);
        Assert.Equal(WorkloadServiceAvailability.Unknown, snapshot.Services.Availability);
        Assert.Equal(Now, snapshot.CapturedAtUtc);
        Assert.Equal(Now, snapshot.LastAttemptAtUtc);
        Assert.False(snapshot.IsStale);
    }

    [Fact]
    public async Task Ssh_timeout_yields_both_unknown_but_fresh()
    {
        var timeout = new WorkloadRemoteResult
        {
            ConnectionResult = new SshConnectionResult
            {
                State = ServerConnectionState.TimedOut,
                ErrorCode = SshConnectionErrorCode.ConnectionTimedOut
            },
            Data = null
        };
        var collector = new WorkloadCollector(new FakeWorkloadRemoteSource(timeout), new FixedTimeProvider(Now));

        var snapshot = await collector.CollectAsync(Server(ServerOperatingSystem.Linux));

        Assert.Equal(DockerAvailability.Unknown, snapshot.Docker.Availability);
        Assert.Equal(WorkloadServiceAvailability.Unknown, snapshot.Services.Availability);
        Assert.Equal(Now, snapshot.LastAttemptAtUtc);
        Assert.False(snapshot.IsStale);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_deterministically()
    {
        var source = new CancellableWorkloadRemoteSource();
        var collector = new WorkloadCollector(source, new FixedTimeProvider(Now));
        using var cancellation = new CancellationTokenSource();

        var collection = collector.CollectAsync(Server(ServerOperatingSystem.Linux), cancellation.Token);
        await source.Entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collection);
    }

    [Fact]
    public async Task Unexpected_remote_exception_degrades_to_unknown_without_escaping()
    {
        var collector = new WorkloadCollector(new ThrowingWorkloadRemoteSource(), new FixedTimeProvider(Now));

        var snapshot = await collector.CollectAsync(Server(ServerOperatingSystem.Linux));

        Assert.Equal(DockerAvailability.Unknown, snapshot.Docker.Availability);
        Assert.Equal(WorkloadServiceAvailability.Unknown, snapshot.Services.Availability);
        Assert.Equal(Now, snapshot.LastAttemptAtUtc);
    }

    [Fact]
    public async Task Successful_linux_pass_maps_docker_and_systemd()
    {
        var data = new WorkloadRawData
        {
            DockerVersion = Ok("27.0.3"),
            DockerPs = Ok("""{"ID":"id","Names":"web","Image":"nginx","State":"running","Status":"Up"}"""),
            SystemdListUnits = Ok("ssh.service loaded active running OpenBSD Secure Shell server"),
            SystemdListUnitFiles = Ok("ssh.service enabled enabled")
        };
        var collector = new WorkloadCollector(new FakeWorkloadRemoteSource(Success(data)), new FixedTimeProvider(Now));

        var snapshot = await collector.CollectAsync(Server(ServerOperatingSystem.Linux));

        Assert.Equal(DockerAvailability.Available, snapshot.Docker.Availability);
        Assert.Single(snapshot.Docker.Containers);
        Assert.Equal(ServiceManager.Systemd, snapshot.Services.Manager);
        Assert.Equal(WorkloadServiceAvailability.Available, snapshot.Services.Availability);
        Assert.Single(snapshot.Services.Services);
        Assert.Equal(Now, snapshot.CapturedAtUtc);
        Assert.False(snapshot.IsStale);
    }

    [Fact]
    public async Task Docker_and_services_fail_independently()
    {
        // Docker daemon down, but systemd is readable — the two views are isolated (§38).
        var data = new WorkloadRawData
        {
            DockerVersion = new RemoteCommandOutcome
            {
                WasExecuted = true,
                ExitStatus = 1,
                StandardError = "Cannot connect to the Docker daemon. Is the docker daemon running?"
            },
            SystemdListUnits = Ok("cron.service loaded active running Regular background program processing daemon")
        };
        var collector = new WorkloadCollector(new FakeWorkloadRemoteSource(Success(data)), new FixedTimeProvider(Now));

        var snapshot = await collector.CollectAsync(Server(ServerOperatingSystem.Linux));

        Assert.Equal(DockerAvailability.Unavailable, snapshot.Docker.Availability);
        Assert.Equal(WorkloadServiceAvailability.Available, snapshot.Services.Availability);
        Assert.Single(snapshot.Services.Services);
    }

    [Fact]
    public async Task Request_defaults_are_docker_on_stats_off()
    {
        var fake = new FakeWorkloadRemoteSource(Success(new WorkloadRawData()));
        var collector = new WorkloadCollector(fake, new FixedTimeProvider(Now));

        await collector.CollectAsync(Server(ServerOperatingSystem.Linux));

        Assert.NotNull(fake.LastRequest);
        Assert.True(fake.LastRequest!.IncludeDocker);
        Assert.False(fake.LastRequest.IncludeContainerStats);
    }

    [Fact]
    public async Task Request_uses_configured_timeout()
    {
        var fake = new FakeWorkloadRemoteSource(Success(new WorkloadRawData()));
        var options = new WorkloadCollectorOptions { Timeout = TimeSpan.FromSeconds(23) };
        var collector = new WorkloadCollector(fake, new FixedTimeProvider(Now), options);

        await collector.CollectAsync(Server(ServerOperatingSystem.Linux));

        Assert.Equal(TimeSpan.FromSeconds(23), fake.LastRequest!.Timeout);
    }

    [Fact]
    public async Task Auto_server_resolved_to_linux_collects_systemd()
    {
        // H-01: an Auto server whose OS the session resolved to Linux must collect systemd, not fall
        // through to Unsupported. The resolved OS arrives via ConnectionResult.DetectedOperatingSystem.
        var data = new WorkloadRawData
        {
            DockerVersion = Ok("27.0.3"),
            DockerPs = Ok("""{"ID":"id","Names":"web","Image":"nginx","State":"running","Status":"Up"}"""),
            SystemdListUnits = Ok("ssh.service loaded active running OpenBSD Secure Shell server")
        };
        var collector = new WorkloadCollector(
            new FakeWorkloadRemoteSource(Success(data, ServerOperatingSystem.Linux)),
            new FixedTimeProvider(Now));

        var snapshot = await collector.CollectAsync(Server(ServerOperatingSystem.Auto));

        Assert.Equal(ServiceManager.Systemd, snapshot.Services.Manager);
        Assert.Equal(WorkloadServiceAvailability.Available, snapshot.Services.Availability);
        Assert.Single(snapshot.Services.Services);
    }

    [Fact]
    public async Task Auto_server_resolved_to_macos_collects_launchd()
    {
        var data = new WorkloadRawData
        {
            LaunchdPrintSystem = Ok("system = {\n\tservices = {\n\t\t123 0 com.apple.sshd\n\t}\n}")
        };
        var collector = new WorkloadCollector(
            new FakeWorkloadRemoteSource(Success(data, ServerOperatingSystem.MacOS)),
            new FixedTimeProvider(Now));

        var snapshot = await collector.CollectAsync(Server(ServerOperatingSystem.Auto));

        Assert.Equal(ServiceManager.Launchd, snapshot.Services.Manager);
        Assert.Equal(WorkloadServiceAvailability.Available, snapshot.Services.Availability);
        Assert.Single(snapshot.Services.Services);
    }

    private static RemoteCommandOutcome Ok(string stdout) => new()
    {
        WasExecuted = true,
        ExitStatus = 0,
        StandardOutput = stdout,
        StandardError = string.Empty
    };

    private static WorkloadRemoteResult Success(WorkloadRawData data) => new()
    {
        ConnectionResult = new SshConnectionResult { State = ServerConnectionState.Connected },
        Data = data
    };

    private static WorkloadRemoteResult Success(WorkloadRawData data, ServerOperatingSystem detectedOs) => new()
    {
        ConnectionResult = new SshConnectionResult
        {
            State = ServerConnectionState.Connected,
            DetectedOperatingSystem = detectedOs
        },
        Data = data
    };

    private static WorkloadRemoteResult Failure() => new()
    {
        ConnectionResult = new SshConnectionResult { State = ServerConnectionState.Unreachable },
        Data = null
    };

    private static Server Server(ServerOperatingSystem os) => new()
    {
        Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Name = "srv",
        Host = "host.example",
        Port = 22,
        Username = "user",
        AuthenticationMethod = AuthenticationMethod.Password,
        CredentialReferenceId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        OperatingSystem = os,
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    private sealed class FakeWorkloadRemoteSource(WorkloadRemoteResult result) : IWorkloadRemoteSource
    {
        public WorkloadRemoteRequest? LastRequest { get; private set; }

        public Task<WorkloadRemoteResult> CollectAsync(
            Server server,
            WorkloadRemoteRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class CancellableWorkloadRemoteSource : IWorkloadRemoteSource
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WorkloadRemoteResult> CollectAsync(
            Server server,
            WorkloadRemoteRequest request,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            var completion = new TaskCompletionSource<WorkloadRemoteResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(
                static state =>
                {
                    var pair = ((TaskCompletionSource<WorkloadRemoteResult>, CancellationToken))state!;
                    pair.Item1.TrySetCanceled(pair.Item2);
                },
                (completion, cancellationToken));
            return completion.Task;
        }
    }

    private sealed class ThrowingWorkloadRemoteSource : IWorkloadRemoteSource
    {
        public Task<WorkloadRemoteResult> CollectAsync(
            Server server,
            WorkloadRemoteRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("remote source failed");
    }
}
