using System.Text;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Probes;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.Infrastructure.SSH;

/// <summary>
/// Runs a registered read once, on request, in its own SSH session. Before any SSH work it checks, in this order, and
/// refuses on the first that fails:
/// <list type="number">
/// <item>the read id is registered (<see cref="RemoteReadRefusal.UnknownRead"/>);</item>
/// <item>the server exists, is monitored and has a credential (<see cref="RemoteReadRefusal.ServerUnavailable"/>), and
/// its operating system is the read's (<see cref="RemoteReadRefusal.WrongOperatingSystem"/>);</item>
/// <item>the argument meets the rule of the read's kind (<see cref="RemoteReadRefusal.InvalidArgument"/>);</item>
/// <item>the argument is in the latest list collected for that server, compared ordinal
/// (<see cref="RemoteReadRefusal.NotListed"/>);</item>
/// <item>no other read is running on that server (<see cref="RemoteReadRefusal.Busy"/>).</item>
/// </list>
/// A read is not a probe: no scheduler, no watch, no history generation, and it never counts in a poll's budget.
/// Nothing here logs the argument, the script or the output.
/// </summary>
public sealed class RemoteReadRunner : IRemoteReadRunner
{
    /// <summary>The time allowed to connect, on top of the read's own timeout.</summary>
    public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);

    private static readonly UTF8Encoding Utf8 = new(false, false);

    private readonly RemoteReadRegistry _registry;
    private readonly IServerService _servers;
    private readonly IServerWorkloadStore _workloads;
    private readonly IRemoteReadTransport _transport;
    private readonly Func<RemoteReadRegistration, string, RemoteReadExec> _buildExec;
    private readonly object _gate = new();
    private readonly HashSet<Guid> _busy = [];

    public RemoteReadRunner(
        RemoteReadRegistry registry,
        IServerService servers,
        IServerWorkloadStore workloads,
        SshConnectionService ssh)
        : this(registry, servers, workloads, (IRemoteReadTransport)(ssh ?? throw new ArgumentNullException(nameof(ssh))), null)
    {
    }

    internal RemoteReadRunner(
        RemoteReadRegistry registry,
        IServerService servers,
        IServerWorkloadStore workloads,
        IRemoteReadTransport transport,
        Func<RemoteReadRegistration, string, RemoteReadExec>? buildExec)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _servers = servers ?? throw new ArgumentNullException(nameof(servers));
        _workloads = workloads ?? throw new ArgumentNullException(nameof(workloads));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _buildExec = buildExec ?? RemoteReadInvocation.Build;
    }

    public async Task<RemoteReadOutcome> RunAsync(
        string readId,
        Guid serverId,
        string argument,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_registry.TryGet(readId, out var read))
        {
            return RemoteReadOutcome.Refused(RemoteReadRefusal.UnknownRead);
        }

        var servers = await _servers.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var server = servers.FirstOrDefault(candidate => candidate.Id == serverId);
        if (server is null || !IsMonitored(server) || !HasCredential(server))
        {
            return RemoteReadOutcome.Refused(RemoteReadRefusal.ServerUnavailable);
        }

        var snapshot = _workloads.Get(serverId);
        if (OperatingSystemOf(server, snapshot) != read.OperatingSystem)
        {
            return RemoteReadOutcome.Refused(RemoteReadRefusal.WrongOperatingSystem);
        }

        if (!RemoteReadArgument.IsValid(read.ArgumentKind, argument))
        {
            return RemoteReadOutcome.Refused(RemoteReadRefusal.InvalidArgument);
        }

        if (!IsListed(snapshot, read.ArgumentKind, argument))
        {
            return RemoteReadOutcome.Refused(RemoteReadRefusal.NotListed);
        }

        lock (_gate)
        {
            if (!_busy.Add(serverId))
            {
                return RemoteReadOutcome.Refused(RemoteReadRefusal.Busy);
            }
        }

        try
        {
            RemoteReadTransportResult result;
            try
            {
                var exec = _buildExec(read, argument);
                result = await _transport
                    .ExecuteAsync(server, exec, ConnectionTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return new RemoteReadOutcome(RemoteReadOutcomeKind.Failed, null, null, null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Classify(result, read);
        }
        finally
        {
            lock (_gate)
            {
                _busy.Remove(serverId);
            }
        }
    }

    private static RemoteReadOutcome Classify(RemoteReadTransportResult result, RemoteReadRegistration read)
    {
        var execution = result.Execution;
        if (execution is { TimedOut: true })
        {
            return new RemoteReadOutcome(RemoteReadOutcomeKind.TimedOut, null, null, null);
        }

        if (result.ErrorCode != SshConnectionErrorCode.None || execution is null)
        {
            return new RemoteReadOutcome(RemoteReadOutcomeKind.Failed, null, null, null);
        }

        if (execution.OutputExceededLimit)
        {
            return new RemoteReadOutcome(RemoteReadOutcomeKind.OutputExceededLimit, null, execution.ExitStatus, null);
        }

        if (!execution.WasExecuted || execution.StandardOutput is null)
        {
            return new RemoteReadOutcome(RemoteReadOutcomeKind.Failed, null, null, null);
        }

        // The SSH layer bounds the read already; this keeps the limit true for any transport.
        return Utf8.GetByteCount(execution.StandardOutput) > read.OutputLimitBytes
            ? new RemoteReadOutcome(RemoteReadOutcomeKind.OutputExceededLimit, null, execution.ExitStatus, null)
            : new RemoteReadOutcome(RemoteReadOutcomeKind.Completed, null, execution.ExitStatus, execution.StandardOutput);
    }

    // The same set the monitoring engine polls.
    private static bool IsMonitored(Server server) =>
        server.OperatingSystem is ServerOperatingSystem.Auto or ServerOperatingSystem.Linux or ServerOperatingSystem.MacOS;

    private static bool HasCredential(Server server) => server.AuthenticationMethod switch
    {
        AuthenticationMethod.Password => server.CredentialReferenceId is { } reference && reference != Guid.Empty,
        AuthenticationMethod.SshKey => !string.IsNullOrWhiteSpace(server.PrivateKeyPath),
        _ => false,
    };

    // A configured OS wins; an Auto server takes the OS its latest service list was collected from.
    private static ServerOperatingSystem OperatingSystemOf(Server server, ServerWorkloadSnapshot? snapshot) =>
        server.OperatingSystem switch
        {
            ServerOperatingSystem.Linux or ServerOperatingSystem.MacOS => server.OperatingSystem,
            _ => snapshot?.Services.Manager switch
            {
                ServiceManager.Systemd => ServerOperatingSystem.Linux,
                ServiceManager.Launchd => ServerOperatingSystem.MacOS,
                _ => ServerOperatingSystem.Unknown,
            },
        };

    private static bool IsListed(ServerWorkloadSnapshot? snapshot, RemoteReadArgumentKind kind, string argument) =>
        snapshot is not null && kind switch
        {
            RemoteReadArgumentKind.ContainerId => snapshot.Docker.Containers.Any(container =>
                string.Equals(container.FullContainerId, argument, StringComparison.Ordinal)),
            RemoteReadArgumentKind.SystemdUnit => snapshot.Services.Manager == ServiceManager.Systemd
                && snapshot.Services.Services.Any(service => string.Equals(service.Id, argument, StringComparison.Ordinal)),
            _ => false,
        };
}
