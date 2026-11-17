using ServerMonitor.Core.Models;
using ServerMonitor.Core.Probes;

namespace ServerMonitor.App.Services;

/// <summary>
/// Transient, in-memory metrics cache. Nothing here is persisted across app
/// restarts; it exists only to remember the last snapshot per server and to
/// de-duplicate concurrent manual refreshes for the same server.
/// </summary>
public interface IServerMetricsStore
{
    ServerMetricsSnapshot? GetLastSnapshot(Guid serverId);

    Task<ServerMetricsCollectionResult> RefreshAsync(
        Server server,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes like <see cref="RefreshAsync(Server, CancellationToken)"/>, then runs the reserved remote probes in
    /// the same session. The returned task completes with the built-in result as soon as it is ready; the probes go
    /// on afterwards and the store always completes <paramref name="probes"/> when the session is over (or at once
    /// when it is not used). This default runs no probes.
    /// </summary>
    async Task<ServerMetricsCollectionResult> RefreshAsync(
        Server server,
        RemoteProbeReservation? probes,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RefreshAsync(server, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            probes?.Complete();
        }
    }

    void Remove(Guid serverId);
}
