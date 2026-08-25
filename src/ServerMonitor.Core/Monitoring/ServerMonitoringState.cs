using ServerMonitor.Core.Enums;

namespace ServerMonitor.Core.Monitoring;

/// <summary>
/// Runtime monitoring state for one server. Purely in-memory: never persisted, never
/// contains metric values (those live in the metrics store) or secrets. The UI observes
/// this to render health, staleness and the refresh indicator.
/// </summary>
public sealed record ServerMonitoringState
{
    public required Guid ServerId { get; init; }

    public ServerHealth Health { get; init; } = ServerHealth.Unknown;

    public bool IsRefreshing { get; init; }

    /// <summary>When the last collection attempt started, successful or not.</summary>
    public DateTimeOffset? LastAttemptAt { get; init; }

    /// <summary>When metrics were last successfully read. Never moved backwards by a failure.</summary>
    public DateTimeOffset? LastSuccessAt { get; init; }

    /// <summary>Number of consecutive fully-failed cycles. Reset to 0 on any success.</summary>
    public int ConsecutiveFailures { get; init; }

    public MetricsCollectionErrorCode? LastError { get; init; }

    public bool IsStale { get; init; }

    public bool HasEverSucceeded => LastSuccessAt is not null;

    public static ServerMonitoringState Initial(Guid serverId) => new() { ServerId = serverId };
}
