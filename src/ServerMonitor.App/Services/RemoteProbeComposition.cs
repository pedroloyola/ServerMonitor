using Microsoft.Extensions.Logging;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Probes;
using ServerMonitor.Core.Workloads;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.App.Services;

/// <summary>
/// Builds the one <see cref="RemoteProbeScheduler"/> and logs, once at startup, every registered probe it left out.
/// Each line names the probe's position and the reason only: never its command, which is a script that may name
/// paths or tools on the user's servers. The one <see cref="RemoteProbeWatch"/> is handed to it, so a watched-only probe
/// runs while a page watches it. It also builds the one <see cref="IRemoteReadRunner"/> the same way.
/// </summary>
internal static class RemoteProbeComposition
{
    internal static RemoteProbeScheduler CreateScheduler(
        IEnumerable<IRemoteProbe> probes,
        HistoryGeneration generation,
        ILogger logger,
        RemoteProbeWatch? watch = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var scheduler = new RemoteProbeScheduler(probes, generation, timeProvider: null, watch);
        foreach (var reason in scheduler.Rejected)
        {
            logger.LogWarning("A remote probe was left out: {Reason}.", reason);
        }

        return scheduler;
    }

    /// <summary>
    /// Builds the one <see cref="IRemoteReadRunner"/> over the registered reads, keeping them from reusing a probe's id,
    /// and logs, once at startup, every read it left out: position and reason only, never the script.
    /// </summary>
    internal static RemoteReadRunner CreateReadRunner(
        IEnumerable<IRemoteRead> reads,
        RemoteProbeScheduler scheduler,
        IServerService servers,
        IServerWorkloadStore workloads,
        SshConnectionService ssh,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(logger);

        var registry = CreateReadRegistry(reads, scheduler.ProbeIds, logger);
        return new RemoteReadRunner(registry, servers, workloads, ssh);
    }

    internal static RemoteReadRegistry CreateReadRegistry(
        IEnumerable<IRemoteRead> reads,
        IEnumerable<string> probeIds,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var registry = new RemoteReadRegistry(reads, probeIds);
        foreach (var reason in registry.Rejected)
        {
            logger.LogWarning("A remote read was left out: {Reason}.", reason);
        }

        return registry;
    }
}
