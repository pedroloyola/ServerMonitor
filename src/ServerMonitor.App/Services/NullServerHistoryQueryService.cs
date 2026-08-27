using ServerMonitor.Core.History;

namespace ServerMonitor.App.Services;

/// <summary>
/// Always-unavailable history query service. Registered as the default so the History UI resolves in
/// every composition (including QA harnesses that do not run the real history stack) and simply shows
/// "history unavailable" instead of failing.
/// </summary>
public sealed class NullServerHistoryQueryService : IServerHistoryQueryService
{
    public bool IsAvailable => false;

    public Task<ServerHistoryResult> GetHistoryAsync(
        Guid serverId,
        HistoryTimeRange range,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(ServerHistoryResult.Empty(serverId, range, now - range.ToDuration(), now));
    }
}
