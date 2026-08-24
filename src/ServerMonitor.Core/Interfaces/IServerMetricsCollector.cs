using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Interfaces;

public interface IServerMetricsCollector
{
    Task<ServerMetricsCollectionResult> CollectAsync(
        Server server,
        CancellationToken cancellationToken = default);
}
