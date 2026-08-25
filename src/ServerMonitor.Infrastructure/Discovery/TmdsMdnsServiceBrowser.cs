using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.Infrastructure.Discovery;

/// <summary>
/// Production <see cref="IMdnsServiceBrowser"/> backed by Tmds.MDns (MIT, no transitive
/// dependencies). It passively browses the local link for <c>_ssh._tcp</c>. Concrete library
/// types and untrusted wire data remain inside the Infrastructure-internal session.
/// </summary>
public sealed class TmdsMdnsServiceBrowser : IMdnsServiceBrowser, IDisposable
{
    private readonly MdnsServiceBrowserOptions _options;
    private readonly ILogger<TmdsMdnsServiceBrowser> _logger;
    private readonly Func<IMdnsBrowserSession> _sessionFactory;
    private readonly object _sync = new();

    private IMdnsBrowserSession? _session;
    private bool _started;
    private bool _disposed;

    public TmdsMdnsServiceBrowser(
        ILogger<TmdsMdnsServiceBrowser> logger,
        MdnsServiceBrowserOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? MdnsServiceBrowserOptions.Default;
        var clock = timeProvider ?? TimeProvider.System;
        _sessionFactory = () => new TmdsMdnsBrowserSession(clock, _logger);
    }

    internal TmdsMdnsServiceBrowser(
        ILogger<TmdsMdnsServiceBrowser> logger,
        MdnsServiceBrowserOptions options,
        Func<IMdnsBrowserSession> sessionFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    public event EventHandler<DiscoveryObservation>? Found;

    public event EventHandler<DiscoveryObservation>? Updated;

    public event EventHandler<DiscoveryObservation>? Removed;

    public void Start()
    {
        lock (_sync)
        {
            if (_started || _disposed)
            {
                return;
            }

            var session = _sessionFactory();
            session.Found += OnFound;
            session.Updated += OnUpdated;
            session.Removed += OnRemoved;
            var queryIntervalMs = _options.ResolveQueryIntervalMilliseconds();
            try
            {
                session.Start(_options.ServiceType, queryIntervalMs);
                _session = session;
                _started = true;
            }
            catch
            {
                Detach(session);
                BestEffortStopAndDispose(session);
                throw;
            }

            _logger.LogDebug(
                "mDNS browse started for {ServiceType} (query interval {IntervalMs} ms).",
                _options.ServiceType,
                queryIntervalMs);
        }
    }

    public void Stop()
    {
        IMdnsBrowserSession? session;
        lock (_sync)
        {
            if (!_started)
            {
                return;
            }

            session = _session;
            _session = null;
            _started = false;
            if (session is not null)
            {
                Detach(session);
            }
        }

        if (session is not null)
        {
            BestEffortStopAndDispose(session);
        }

        _logger.LogDebug("mDNS browse stopped.");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
    }

    private void OnFound(object? sender, DiscoveryObservation observation) =>
        Found?.Invoke(this, observation);

    private void OnUpdated(object? sender, DiscoveryObservation observation) =>
        Updated?.Invoke(this, observation);

    private void OnRemoved(object? sender, DiscoveryObservation observation) =>
        Removed?.Invoke(this, observation);

    private void Detach(IMdnsBrowserSession session)
    {
        session.Found -= OnFound;
        session.Updated -= OnUpdated;
        session.Removed -= OnRemoved;
    }

    private void BestEffortStopAndDispose(IMdnsBrowserSession session)
    {
        try
        {
            session.Stop();
        }
        catch (Exception exception)
        {
            _logger.LogDebug("mDNS browse stop raised {Type}.", exception.GetType().Name);
        }

        try
        {
            session.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogDebug("mDNS browse disposal raised {Type}.", exception.GetType().Name);
        }
    }
}
