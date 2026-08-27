using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.Services;

/// <summary>
/// UI-facing history reads (ADR-015 §8; spec §34, §50, §54). Resolves a range to UTC bounds, runs the
/// store query and downsampling on a background thread (never the UI thread, never <c>.Result</c>),
/// and honors cancellation so a superseded query cannot overwrite a newer selection.
/// </summary>
public sealed class ServerHistoryQueryService : IServerHistoryQueryService
{
    private readonly IServerHistoryStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ServerHistoryQueryService> _logger;

    public ServerHistoryQueryService(
        IServerHistoryStore store,
        ILogger<ServerHistoryQueryService> logger,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsAvailable => _store.IsAvailable;

    public async Task<ServerHistoryResult> GetHistoryAsync(
        Guid serverId,
        HistoryTimeRange range,
        CancellationToken cancellationToken = default)
    {
        var end = _timeProvider.GetUtcNow();
        var start = end - range.ToDuration();

        if (!_store.IsAvailable)
        {
            return ServerHistoryResult.Empty(serverId, range, start, end);
        }

        // Offload the synchronous SQLite work + downsampling so a UI-thread caller never blocks.
        return await Task.Run(async () =>
        {
            var samples = await _store.QueryAsync(serverId, start, end, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return new ServerHistoryResult
            {
                ServerId = serverId,
                Range = range,
                StartUtc = start,
                EndUtc = end,
                Cpu = HistoryDownsampler.Build(samples, start, end, static s => s.CpuPercent),
                Memory = HistoryDownsampler.Build(samples, start, end, static s => s.MemoryPercent),
                Disk = HistoryDownsampler.Build(samples, start, end, static s => s.DiskPercent),
                ContainsOfflineSamples = samples.Any(static sample => sample.Health == ServerHealth.Offline)
            };
        }, cancellationToken).ConfigureAwait(false);
    }
}
