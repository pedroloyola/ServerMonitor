using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Monitoring;

/// <summary>
/// The result of a single <b>fresh</b> monitoring cycle, published by the engine the moment a
/// cycle completes. This is the only unambiguous fresh-data signal (see ADR-015 §2/§3): the UI may
/// keep the last valid snapshot as <i>stale</i>, but this carries the fresh outcome. When the cycle
/// failed, <see cref="Snapshot"/> is <c>null</c> — history must record <c>null</c> metrics here,
/// never a recycled stale value.
/// </summary>
public sealed record MonitoringCycleCompletion
{
    public required Guid ServerId { get; init; }

    /// <summary>Attempt/completion time in UTC (from the engine's injected <see cref="TimeProvider"/>).</summary>
    public required DateTimeOffset CapturedAtUtc { get; init; }

    public required MonitoringOutcome Outcome { get; init; }

    public required ServerHealth Health { get; init; }

    /// <summary>The fresh snapshot, or <c>null</c> when this cycle produced no usable data.</summary>
    public ServerMetricsSnapshot? Snapshot { get; init; }
}

/// <summary>
/// Observes completed monitoring cycles as a non-blocking, degradable side effect. Implementations
/// must never throw, block, or perform I/O on the calling (engine) thread. The engine depends only
/// on this abstraction and has no knowledge of history or SQLite (ADR-015 §3).
/// </summary>
public interface IMonitoringCycleObserver
{
    void OnCycleCompleted(MonitoringCycleCompletion completion);
}

/// <summary>Default no-op observer, so the engine runs identically when history is absent.</summary>
public sealed class NullMonitoringCycleObserver : IMonitoringCycleObserver
{
    public static readonly NullMonitoringCycleObserver Instance = new();

    private NullMonitoringCycleObserver()
    {
    }

    public void OnCycleCompleted(MonitoringCycleCompletion completion)
    {
    }
}
