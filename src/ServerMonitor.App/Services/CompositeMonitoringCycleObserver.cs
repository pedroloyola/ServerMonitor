using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Services;

/// <summary>
/// Fans a completed monitoring cycle out to several <see cref="IMonitoringCycleObserver"/>s, isolating
/// each in its own try/catch. The <c>MonitoringEngine</c> still sees a single observer, so nothing on the
/// engine thread changes; this only lets the M11 workload observer ride the same cycle signal as the M10
/// history recorder without either being able to break the other (§38). A faulty observer is logged and
/// skipped; the remaining observers still run. Order is deterministic (as supplied) — history first, so
/// the M10 behavior is unchanged.
/// </summary>
public sealed class CompositeMonitoringCycleObserver : IMonitoringCycleObserver
{
    private readonly IReadOnlyList<IMonitoringCycleObserver> _observers;
    private readonly ILogger<CompositeMonitoringCycleObserver> _logger;

    public CompositeMonitoringCycleObserver(
        IEnumerable<IMonitoringCycleObserver> observers,
        ILogger<CompositeMonitoringCycleObserver> logger)
    {
        ArgumentNullException.ThrowIfNull(observers);
        _observers = observers.ToArray();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void OnCycleCompleted(MonitoringCycleCompletion completion)
    {
        // The observer contract is non-throwing, but we defend anyway: one observer must never stop
        // another from seeing the cycle, and neither must ever break the engine.
        foreach (var observer in _observers)
        {
            try
            {
                observer.OnCycleCompleted(completion);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Cycle observer {Observer} threw and was isolated. Type: {Type}.",
                    observer.GetType().Name,
                    exception.GetType().Name);
            }
        }
    }
}
