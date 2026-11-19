using Microsoft.Extensions.Logging;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Probes;

namespace ServerMonitor.App.Services;

/// <summary>
/// Builds the one <see cref="RemoteProbeScheduler"/> and logs, once at startup, every registered probe it left out.
/// Each line names the probe's position and the reason only: never its command, which is a script that may name
/// paths or tools on the user's servers. The one <see cref="RemoteProbeWatch"/> is handed to it, so a watched-only probe
/// runs while a page watches it.
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
}
