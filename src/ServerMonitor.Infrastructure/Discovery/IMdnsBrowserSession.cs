using ServerMonitor.Core.Discovery;

namespace ServerMonitor.Infrastructure.Discovery;

/// <summary>
/// Infrastructure-internal lifetime seam around the concrete mDNS library. It deliberately uses
/// only Core observations, keeping Tmds.MDns types out of public contracts and allowing startup
/// cleanup to be exercised without multicast sockets.
/// </summary>
internal interface IMdnsBrowserSession : IDisposable
{
    event EventHandler<DiscoveryObservation>? Found;

    event EventHandler<DiscoveryObservation>? Updated;

    event EventHandler<DiscoveryObservation>? Removed;

    void Start(string serviceType, int queryIntervalMilliseconds);

    void Stop();
}
