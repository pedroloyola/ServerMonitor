using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Monitoring;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Services;

/// <summary>
/// Second consumer of the M6 cycle signal (after the M10 history recorder), riding it as a tick to
/// schedule read-only workload collection — with no timer of its own (§34). On each fresh cycle it
/// applies the pure <see cref="WorkloadCadencePolicy"/> (default 60s per server) and, when due, does a
/// single non-blocking enqueue of a scheduled <see cref="WorkloadRequest"/>. It performs no I/O and never
/// blocks the engine thread (same discipline as <c>HistoryRecorder</c>); the actual SSH collection runs
/// in <see cref="WorkloadCollectorService"/>.
/// </summary>
public sealed class WorkloadCadenceObserver : IMonitoringCycleObserver
{
    private readonly WorkloadRequestQueue _queue;
    private readonly WorkloadCadencePolicy _policy;
    private readonly ILogger<WorkloadCadenceObserver> _logger;

    // One monitoring loop per server ⇒ writes to a given key never race across cycles.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastEnqueuedUtc = new();
    private long _dropCount;

    public WorkloadCadenceObserver(
        WorkloadRequestQueue queue,
        ILogger<WorkloadCadenceObserver> logger,
        WorkloadCadencePolicy? policy = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _policy = policy ?? new WorkloadCadencePolicy();
    }

    public void OnCycleCompleted(MonitoringCycleCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        // A cancelled cycle carries no fresh signal (shutdown/superseded) — ignore, like HistoryRecorder.
        if (completion.Outcome == MonitoringOutcome.Cancelled)
        {
            return;
        }

        var candidate = completion.CapturedAtUtc;
        var last = _lastEnqueuedUtc.TryGetValue(completion.ServerId, out var value) ? value : (DateTimeOffset?)null;
        if (!_policy.IsDue(last, candidate))
        {
            return;
        }

        // Advance the cadence marker whether or not the enqueue succeeds, so a dropped request becomes an
        // observable gap rather than a tight retry against a full queue (same rule as M10).
        _lastEnqueuedUtc[completion.ServerId] = candidate;

        var request = new WorkloadRequest
        {
            ServerId = completion.ServerId,
            Reason = WorkloadRefreshReason.Scheduled
        };

        if (!_queue.TryEnqueueScheduled(request))
        {
            var total = Interlocked.Increment(ref _dropCount);
            if (total == 1 || total % 100 == 0)
            {
                _logger.LogWarning(
                    "Workload queue full; dropped {Total} scheduled request(s) so far. Monitoring is unaffected.",
                    total);
            }
        }
    }

    /// <summary>Forgets a server's cadence marker (e.g. after removal) so its next cycle schedules.</summary>
    public void Forget(Guid serverId) => _lastEnqueuedUtc.TryRemove(serverId, out _);
}
