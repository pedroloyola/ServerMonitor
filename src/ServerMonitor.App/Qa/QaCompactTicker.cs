using Microsoft.Extensions.Hosting;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Qa;

/// <summary>What each tick of <see cref="QaCompactTicker"/> publishes.</summary>
internal enum QaCompactTickerMode
{
    /// <summary>Every visible server's CURRENT state again, unchanged (an engine cycle that changes nothing).</summary>
    Identical,

    /// <summary>Readings that move deterministically around the scenario's values; health re-derived by the engine rule.</summary>
    Varying
}

/// <summary>
/// QA-ONLY (UI.8 §3). A deterministic stand-in for the engine's cycle: every <see cref="Interval"/> it publishes one
/// state per visible server into the REAL <see cref="IServerMonitoringStateStore"/>, from a timer thread, so the change
/// travels the production path (<c>StateChanged</c> → dispatcher → <c>DashboardViewModel.ApplyMonitoringState</c>) and
/// PropertyChanged / realization / CPU cost per sample can be measured on the real window. The timer comes from the
/// injected <see cref="TimeProvider"/> (a fake one in tests); the values are a pure function of the tick number. It
/// never touches SSH, persistence or the presentation clock. Excluded from Release.
/// </summary>
internal sealed class QaCompactTicker(
    QaCompactCatalog catalog,
    QaCompactMetricsStore metrics,
    IServerMonitoringStateStore states,
    QaCompactTickerMode mode,
    TimeProvider time) : IHostedService, IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private ITimer? _timer;
    private int _ticks;

    public QaCompactTickerMode Mode => mode;

    /// <summary>Ticks published so far.</summary>
    public int Ticks => Volatile.Read(ref _ticks);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _timer ??= time.CreateTimer(_ => Tick(), null, Interval, Interval);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>One sample for every visible server; driven by the timer only.</summary>
    private void Tick()
    {
        var tick = Interlocked.Increment(ref _ticks);
        foreach (var entry in catalog.Entries)
        {
            if (entry.Server.IsHidden)
            {
                continue;
            }

            var id = entry.Server.Id;
            if (mode == QaCompactTickerMode.Varying
                && entry.Snapshot is { } baseline
                && entry.State.Health is not ServerHealth.Offline and not ServerHealth.Unknown)
            {
                var next = baseline with
                {
                    CpuUsagePercent = Vary(baseline.CpuUsagePercent, tick, 0),
                    MemoryUsagePercent = Vary(baseline.MemoryUsagePercent, tick, 1),
                    DiskUsagePercent = Vary(baseline.DiskUsagePercent, tick, 2)
                };
                metrics.Replace(next);
                states.Set(states.Get(id) with { Health = HealthEvaluator.EvaluateFromMetrics(next) });
            }
            else
            {
                states.Set(states.Get(id));
            }
        }
    }

    // ±6 points around the baseline in a fixed 5-step cycle, offset per metric; an unknown metric stays unknown.
    internal static double? Vary(double? baseline, int tick, int offset) =>
        baseline is { } value ? Math.Clamp(value + (((tick + offset) % 5) - 2) * 3, 0, 100) : null;
}
