using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Discovery;
using Tmds.MDns;

namespace ServerMonitor.Infrastructure.Discovery;

/// <summary>Contains every reference to the concrete Tmds.MDns event model.</summary>
internal sealed class TmdsMdnsBrowserSession(
    TimeProvider timeProvider,
    ILogger<TmdsMdnsServiceBrowser> logger) : IMdnsBrowserSession
{
    private readonly ServiceBrowser _browser = new();
    private bool _subscribed;
    private bool _stopped;

    public event EventHandler<DiscoveryObservation>? Found;

    public event EventHandler<DiscoveryObservation>? Updated;

    public event EventHandler<DiscoveryObservation>? Removed;

    public void Start(string serviceType, int queryIntervalMilliseconds)
    {
        _browser.QueryParameters.QueryInterval = queryIntervalMilliseconds;
        _browser.ServiceAdded += OnServiceAdded;
        _browser.ServiceChanged += OnServiceChanged;
        _browser.ServiceRemoved += OnServiceRemoved;
        _subscribed = true;
        _browser.StartBrowse(serviceType);
    }

    public void Stop()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        DetachHandlers();
        _browser.StopBrowse();
    }

    public void Dispose()
    {
        // ServiceBrowser has no IDisposable contract. Detaching first prevents retained callback
        // targets; StopBrowse releases its active network resources.
        DetachHandlers();
        if (!_stopped)
        {
            _stopped = true;
            _browser.StopBrowse();
        }
    }

    private void OnServiceAdded(object? sender, ServiceAnnouncementEventArgs args) =>
        Raise(args, Found, "found");

    private void OnServiceChanged(object? sender, ServiceAnnouncementEventArgs args) =>
        Raise(args, Updated, "updated");

    private void OnServiceRemoved(object? sender, ServiceAnnouncementEventArgs args) =>
        Raise(args, Removed, "removed");

    private void Raise(
        ServiceAnnouncementEventArgs args,
        EventHandler<DiscoveryObservation>? handler,
        string verb)
    {
        if (handler is null)
        {
            return;
        }

        var announcement = args.Announcement;
        if (announcement is null)
        {
            return;
        }

        // TXT records are deliberately ignored (not read, not retained).
        var observation = DiscoveryInputPolicy.TryCreateObservation(
            announcement.Instance,
            announcement.Type,
            announcement.Domain,
            announcement.Hostname,
            announcement.Port,
            announcement.Addresses,
            announcement.NetworkInterface?.Id,
            timeProvider.GetUtcNow());
        if (observation is null)
        {
            return;
        }

        logger.LogDebug("mDNS service {Verb}: {Host}:{Port}.", verb, observation.HostName, observation.Port);
        handler.Invoke(this, observation);
    }

    private void DetachHandlers()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        _browser.ServiceAdded -= OnServiceAdded;
        _browser.ServiceChanged -= OnServiceChanged;
        _browser.ServiceRemoved -= OnServiceRemoved;
    }
}
