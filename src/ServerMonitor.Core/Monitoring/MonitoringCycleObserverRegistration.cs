namespace ServerMonitor.Core.Monitoring;

/// <summary>
/// An additional cycle observer supplied by a build's composition. The app appends every registration's
/// <see cref="Observer"/> after its built-in observers, each isolated from them and from the others like
/// the built-in ones (ADR-027 §5). It is its own type, so registering it never shadows an existing
/// service. The source build registers none.
/// </summary>
public sealed record MonitoringCycleObserverRegistration
{
    public MonitoringCycleObserverRegistration(IMonitoringCycleObserver observer)
    {
        Observer = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    public IMonitoringCycleObserver Observer { get; }
}
