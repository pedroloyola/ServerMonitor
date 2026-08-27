using ServerMonitor.Collectors.Workloads.Mapping;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Workloads;
using ServerMonitor.Infrastructure.Collectors.Workloads;

namespace ServerMonitor.Collectors.Workloads;

/// <summary>
/// Collects read-only workload observability (Docker containers + managed services) for one server over
/// the fixed <see cref="IWorkloadRemoteSource"/> port, then maps the raw output to Core models with pure
/// parsers/mappers. Mirrors the metrics collectors: it never touches SSH.NET directly, and it never
/// throws for expected remote failures — those are encoded as availabilities (§38). Docker and services
/// are isolated; a total SSH failure yields both views <see cref="DockerAvailability.Unknown"/> /
/// <see cref="WorkloadServiceAvailability.Unknown"/>. The result always describes a single fresh attempt
/// (<see cref="ServerWorkloadSnapshot.IsStale"/> = <c>false</c>); stale carry-over is the caller's job.
/// </summary>
public sealed class WorkloadCollector : IWorkloadCollector
{
    private readonly IWorkloadRemoteSource _remoteSource;
    private readonly TimeProvider _timeProvider;
    private readonly WorkloadCollectorOptions _options;

    public WorkloadCollector(
        IWorkloadRemoteSource remoteSource,
        TimeProvider? timeProvider = null,
        WorkloadCollectorOptions? options = null)
    {
        _remoteSource = remoteSource ?? throw new ArgumentNullException(nameof(remoteSource));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? WorkloadCollectorOptions.Default;
    }

    public async Task<ServerWorkloadSnapshot> CollectAsync(
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var attemptedAt = _timeProvider.GetUtcNow();

        try
        {
            var request = new WorkloadRemoteRequest
            {
                IncludeDocker = true,
                IncludeContainerStats = false, // §58: docker stats deferred.
                Timeout = _options.Timeout
            };

            var remote = await _remoteSource.CollectAsync(server, request, cancellationToken).ConfigureAwait(false);

            if (!remote.ConnectionResult.IsSuccess || remote.Data is not { } data)
            {
                // Total SSH failure (trust/auth/transport/timeout): both views Unknown, but still fresh.
                return Fresh(server.Id, attemptedAt, DockerSnapshot.Unknown, ServiceSnapshot.Unknown);
            }

            var docker = DockerWorkloadMapper.Map(data.DockerVersion, data.DockerPs);

            // Use the OS the session actually resolved (Auto is detected in-session via uname); fall back
            // to the configured OS only when the session could not report one. This is what makes an Auto
            // server collect systemd/launchd instead of falling through to Unsupported (H-01).
            var effectiveOs = remote.ConnectionResult.DetectedOperatingSystem;
            if (effectiveOs is ServerOperatingSystem.Unknown or ServerOperatingSystem.Auto)
            {
                effectiveOs = server.OperatingSystem;
            }

            var services = ServiceWorkloadMapper.Map(
                effectiveOs,
                data.SystemdListUnits,
                data.SystemdListUnitFiles,
                data.LaunchdPrintSystem);

            return Fresh(server.Id, attemptedAt, docker, services);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A collector must never crash its caller over one server; degrade to Unknown/Unknown.
            return Fresh(server.Id, attemptedAt, DockerSnapshot.Unknown, ServiceSnapshot.Unknown);
        }
    }

    private static ServerWorkloadSnapshot Fresh(
        Guid serverId,
        DateTimeOffset attemptedAt,
        DockerSnapshot docker,
        ServiceSnapshot services) => new()
    {
        ServerId = serverId,
        CapturedAtUtc = attemptedAt,
        LastAttemptAtUtc = attemptedAt,
        IsStale = false,
        Docker = docker,
        Services = services
    };
}
