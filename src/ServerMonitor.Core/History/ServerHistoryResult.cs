namespace ServerMonitor.Core.History;

/// <summary>
/// The chart-ready result of a history query for one server over one range: three downsampled
/// series plus the resolved UTC bounds. Produced off the UI thread; consumed by the History VM.
/// </summary>
public sealed record ServerHistoryResult
{
    public required Guid ServerId { get; init; }

    public required HistoryTimeRange Range { get; init; }

    public required DateTimeOffset StartUtc { get; init; }

    public required DateTimeOffset EndUtc { get; init; }

    public required HistorySeries Cpu { get; init; }

    public required HistorySeries Memory { get; init; }

    public required HistorySeries Disk { get; init; }

    /// <summary>True when the queried range contains at least one sample captured while the
    /// server was offline. The UI uses this metadata to explain that the corresponding null
    /// sections are intentional offline gaps rather than zero-valued measurements.</summary>
    public bool ContainsOfflineSamples { get; init; }

    /// <summary>True only when the range has neither a usable metric nor an observed offline
    /// sample. A fully-offline range is real history and must be presented as such, not as an
    /// empty database.</summary>
    public bool IsEmpty => !ContainsOfflineSamples && !Cpu.HasData && !Memory.HasData && !Disk.HasData;

    public static ServerHistoryResult Empty(Guid serverId, HistoryTimeRange range, DateTimeOffset startUtc, DateTimeOffset endUtc) => new()
    {
        ServerId = serverId,
        Range = range,
        StartUtc = startUtc,
        EndUtc = endUtc,
        Cpu = HistorySeries.Empty,
        Memory = HistorySeries.Empty,
        Disk = HistorySeries.Empty
    };
}
