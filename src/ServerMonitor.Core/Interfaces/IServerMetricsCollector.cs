using ServerMonitor.Core.Models;
using ServerMonitor.Core.Probes;

namespace ServerMonitor.Core.Interfaces;

public interface IServerMetricsCollector
{
    Task<ServerMetricsCollectionResult> CollectAsync(
        Server server,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Two-phase collection in one SSH session. <see cref="MetricsCollectionPhases.OnBaseResult"/> is called at most
    /// once, with the built-in result, as soon as it is ready; the remote probes of
    /// <see cref="MetricsCollectionPhases.Probes"/> then run, and the returned task completes when the session is over.
    /// When the callback was called, the returned result is the same one and must not be published again; when it was
    /// not (a failed connection or built-in phase, or a collector without a second phase), publish the returned result
    /// as usual. This default never calls back and runs no probes.
    /// </summary>
    Task<ServerMetricsCollectionResult> CollectAsync(
        Server server,
        MetricsCollectionPhases phases,
        CancellationToken cancellationToken = default) =>
        CollectAsync(server, cancellationToken);
}

/// <summary>The two phases of one metrics collection (see <see cref="IServerMetricsCollector"/>).</summary>
/// <param name="OnBaseResult">Receives the built-in result once, before the probes run.</param>
/// <param name="Probes">The reserved second phase, or null to run the built-in commands only.</param>
public sealed record MetricsCollectionPhases(
    Action<ServerMetricsCollectionResult> OnBaseResult,
    RemoteProbeReservation? Probes);
