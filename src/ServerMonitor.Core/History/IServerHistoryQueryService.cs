namespace ServerMonitor.Core.History;

/// <summary>
/// High-level history read path for the UI. The UI never writes SQL: it asks for a server + range
/// and receives a downsampled, chart-ready <see cref="ServerHistoryResult"/>. Runs off the UI thread
/// and honors cancellation so rapid range switches never race (spec §34, §50, §51, §80).
/// </summary>
public interface IServerHistoryQueryService
{
    /// <summary>True when the underlying store is usable; the UI shows "history unavailable" otherwise.</summary>
    bool IsAvailable { get; }

    Task<ServerHistoryResult> GetHistoryAsync(
        Guid serverId,
        HistoryTimeRange range,
        CancellationToken cancellationToken = default);
}
