using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// Scripted, synchronous mDNS seam. Tests decide exactly when Found/Updated/Removed are
/// delivered and can inspect lifecycle/subscription counts without opening sockets.
/// </summary>
internal sealed class FakeMdnsServiceBrowser : IMdnsServiceBrowser
{
    private EventHandler<DiscoveryObservation>? _found;
    private EventHandler<DiscoveryObservation>? _updated;
    private EventHandler<DiscoveryObservation>? _removed;

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int FoundSubscriberCount => _found?.GetInvocationList().Length ?? 0;

    public int UpdatedSubscriberCount => _updated?.GetInvocationList().Length ?? 0;

    public int RemovedSubscriberCount => _removed?.GetInvocationList().Length ?? 0;

    public Exception? StartException { get; set; }

    public event EventHandler<DiscoveryObservation>? Found
    {
        add
        {
            _found += value;
        }
        remove
        {
            _found -= value;
        }
    }

    public event EventHandler<DiscoveryObservation>? Updated
    {
        add
        {
            _updated += value;
        }
        remove
        {
            _updated -= value;
        }
    }

    public event EventHandler<DiscoveryObservation>? Removed
    {
        add
        {
            _removed += value;
        }
        remove
        {
            _removed -= value;
        }
    }

    public void Start()
    {
        StartCount++;
        if (StartException is { } exception)
        {
            throw exception;
        }
    }

    public void Stop() => StopCount++;

    public void EmitFound(DiscoveryObservation observation) => _found?.Invoke(this, observation);

    public EventHandler<DiscoveryObservation>? CaptureFoundHandler() => _found;

    public void EmitCaptured(
        EventHandler<DiscoveryObservation>? handler,
        DiscoveryObservation observation) => handler?.Invoke(this, observation);

    public void EmitUpdated(DiscoveryObservation observation) => _updated?.Invoke(this, observation);

    public void EmitRemoved(DiscoveryObservation observation) => _removed?.Invoke(this, observation);
}
