using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY <see cref="IServerHistoryQueryService"/>. Generates a deterministic dataset per scenario
/// and runs it through the <b>real</b> <see cref="HistoryDownsampler"/>, so the harness exercises the
/// exact production query/downsampling path. The "DB unavailable" scenario throws, so the History
/// page's unavailable state can be inspected without corrupting a real database.
/// </summary>
internal sealed class QaServerHistoryQueryService : IServerHistoryQueryService
{
    public bool IsAvailable => true;

    public Task<ServerHistoryResult> GetHistoryAsync(
        Guid serverId,
        HistoryTimeRange range,
        CancellationToken cancellationToken = default)
    {
        var end = QaHistoryCatalog.Now;
        var start = end - range.ToDuration();
        var scenario = QaHistoryCatalog.For(serverId);
        if (scenario is null)
        {
            return Task.FromResult(ServerHistoryResult.Empty(serverId, range, start, end));
        }

        if (scenario.Kind == QaHistoryKind.Unavailable)
        {
            throw new InvalidOperationException("QA: history store unavailable.");
        }

        var samples = QaHistoryCatalog.Generate(scenario, start, end);
        return Task.FromResult(new ServerHistoryResult
        {
            ServerId = serverId,
            Range = range,
            StartUtc = start,
            EndUtc = end,
            Cpu = HistoryDownsampler.Build(samples, start, end, static s => s.CpuPercent),
            Memory = HistoryDownsampler.Build(samples, start, end, static s => s.MemoryPercent),
            Disk = HistoryDownsampler.Build(samples, start, end, static s => s.DiskPercent),
            ContainsOfflineSamples = samples.Any(static sample => sample.Health == ServerHealth.Offline)
        });
    }
}
