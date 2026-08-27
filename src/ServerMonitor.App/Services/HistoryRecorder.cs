using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Services;

/// <summary>
/// Turns fresh monitoring cycles into history samples (ADR-015 §2/§3/§6). Observes the engine on its
/// own thread and must stay non-blocking: it applies the sampling policy, sanitizes metrics, and does
/// a single non-blocking enqueue — never I/O, never a lock the engine waits on. Fresh-vs-stale is
/// honored by construction: it reads only the cycle's fresh snapshot (null on failure), so an offline
/// cycle records <c>null</c> metrics + its health, never a recycled stale value.
/// </summary>
public sealed class HistoryRecorder : IMonitoringCycleObserver
{
    private readonly HistorySampleChannel _channel;
    private readonly HistorySamplingPolicy _policy;
    private readonly ILogger<HistoryRecorder> _logger;

    // One monitoring loop exists per server, so writes to a given key never race; the concurrent
    // dictionary only guards cross-server concurrency.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastPersistedUtc = new();
    private long _dropCount;

    public HistoryRecorder(
        HistorySampleChannel channel,
        ILogger<HistoryRecorder> logger,
        HistorySamplingPolicy? policy = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _policy = policy ?? new HistorySamplingPolicy();
    }

    public void OnCycleCompleted(MonitoringCycleCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        // A cancelled cycle carries no fresh measurement and leaves state untouched — never recorded.
        if (completion.Outcome == MonitoringOutcome.Cancelled)
        {
            return;
        }

        var capturedAt = completion.CapturedAtUtc;
        if (!HistorySampleValidator.IsValidTimestamp(capturedAt))
        {
            return;
        }

        var last = _lastPersistedUtc.TryGetValue(completion.ServerId, out var value) ? value : (DateTimeOffset?)null;
        if (!_policy.ShouldPersist(last, capturedAt))
        {
            return;
        }

        // Advance the cadence marker whether or not the enqueue succeeds: a dropped sample becomes an
        // observable gap rather than a tight retry against a full queue.
        _lastPersistedUtc[completion.ServerId] = capturedAt;

        var snapshot = completion.Snapshot;
        var sample = new ServerHistorySample
        {
            ServerId = completion.ServerId,
            CapturedAtUtc = capturedAt,
            Health = completion.Health,
            CpuPercent = HistorySampleValidator.SanitizePercent(snapshot?.CpuUsagePercent),
            MemoryPercent = HistorySampleValidator.SanitizePercent(snapshot?.MemoryUsagePercent),
            DiskPercent = HistorySampleValidator.SanitizePercent(snapshot?.DiskUsagePercent)
        };

        if (!_channel.TryWrite(sample))
        {
            var total = Interlocked.Increment(ref _dropCount);
            // Log coarsely so a stalled database cannot spam the log (and never a popup, spec §28).
            if (total == 1 || total % 100 == 0)
            {
                _logger.LogWarning(
                    "History queue full; dropped {Total} sample(s) so far. Monitoring is unaffected.",
                    total);
            }
        }
    }

    /// <summary>Forgets a server's cadence marker (e.g. after Clear history) so its next cycle records.</summary>
    public void Forget(Guid serverId) => _lastPersistedUtc.TryRemove(serverId, out _);

    public void ForgetAll() => _lastPersistedUtc.Clear();
}
