using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Services;

/// <summary>
/// Runtime discovery store and high-level <see cref="IServerDiscoveryService"/>. It subscribes to
/// the fakeable browser seam (<see cref="IMdnsServiceBrowser"/>), merges per-interface
/// observations of the same instance by <see cref="ServiceInstanceIdentity"/> (never by IP),
/// tracks first/last seen, expires silent observations and applies a short grace to removals —
/// all through an injected <see cref="TimeProvider"/>. It runs as an <see cref="IHostedService"/>
/// tied to the app host, raises <see cref="DiscoveredChanged"/> only on material changes, and
/// never touches SSH, credentials, trust or metrics.
/// </summary>
/// <remarks>
/// The lifecycle (start/stop/dispose) is serialized behind a dedicated async gate and a small
/// state machine, so a start and a stop cannot interleave, a failed or cancelled start rolls back
/// cleanly (and never poisons a later start), stop is idempotent and drain-safe, and dispose
/// fences any future start. The store mutations use a separate monitor lock.
/// </remarks>
public sealed class ServerDiscoveryService : IServerDiscoveryService, IHostedService, IAsyncDisposable
{
    private enum LifecycleState
    {
        Stopped,
        Started,
        Disposed
    }

    private readonly IMdnsServiceBrowser _browser;
    private readonly IIgnoredDeviceStore _ignoredStore;
    private readonly TimeProvider _timeProvider;
    private readonly DiscoveryOptions _options;
    private readonly ILogger<ServerDiscoveryService> _logger;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<ServiceInstanceIdentity, TrackedService> _tracked = [];
    private readonly Dictionary<ServiceInstanceIdentity, string> _published = [];
    private HashSet<string> _ignored = new(StringComparer.Ordinal);

    // Keep a full visible-set reserve even when ignored identities flood the segment. Ignored
    // observations are useful for Reset semantics, but they may consume only the other half of
    // this bounded runtime store unless an ignored entry is evicted for a visible candidate.
    private const int MaxTrackedServices = DiscoveryInputPolicy.MaxVisibleServices * 2;
    private static readonly TimeSpan MinimumNotificationDelay = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MaximumNotificationDelay = TimeSpan.FromSeconds(1);

    private LifecycleState _state = LifecycleState.Stopped;
    private long _lifecycleGeneration;
    private bool _acceptBrowserCallbacks;
    private EventHandler<DiscoveryObservation>? _foundHandler;
    private EventHandler<DiscoveryObservation>? _updatedHandler;
    private EventHandler<DiscoveryObservation>? _removedHandler;
    private CancellationTokenSource? _sweepCts;
    private Task _sweepLoop = Task.CompletedTask;
    private CancellationTokenSource? _notificationCts;
    private Task _notificationTask = Task.CompletedTask;
    private long _materialChangeVersion;

    public ServerDiscoveryService(
        IMdnsServiceBrowser browser,
        IIgnoredDeviceStore ignoredStore,
        ILogger<ServerDiscoveryService> logger,
        TimeProvider? timeProvider = null,
        DiscoveryOptions? options = null)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _ignoredStore = ignoredStore ?? throw new ArgumentNullException(nameof(ignoredStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? DiscoveryOptions.Default;
    }

    public event EventHandler? DiscoveredChanged;

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => StartAsync(cancellationToken);

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != LifecycleState.Stopped)
            {
                // Already started, or disposed: idempotent no-op / permanently fenced.
                return;
            }

            CancellationTokenSource? sweepCts = null;
            try
            {
                var ignored = await _ignoredStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                long generation;
                lock (_sync)
                {
                    _ignored = new HashSet<string>(ignored, StringComparer.Ordinal);
                    generation = ++_lifecycleGeneration;
                    _acceptBrowserCallbacks = true;
                    _foundHandler = (_, observation) => OnFoundOrUpdated(generation, observation);
                    _updatedHandler = (_, observation) => OnFoundOrUpdated(generation, observation);
                    _removedHandler = (_, observation) => OnRemoved(generation, observation);
                }

                _browser.Found += _foundHandler;
                _browser.Updated += _updatedHandler;
                _browser.Removed += _removedHandler;
                _browser.Start();

                sweepCts = new CancellationTokenSource();
                var token = sweepCts.Token;
                _sweepCts = sweepCts;
                _sweepLoop = Task.Run(() => SweepLoopAsync(generation, token), CancellationToken.None);

                _state = LifecycleState.Started;
                _logger.LogDebug("Discovery service started.");
            }
            catch (Exception exception)
            {
                // Roll back a partial start so a later start can retry; never poison the service.
                await RollbackStartAsync(sweepCts).ConfigureAwait(false);
                if (exception is OperationCanceledException)
                {
                    throw;
                }

                _logger.LogError(
                    "Discovery service failed to start; discovery is disabled for now. Exception type: {Type}.",
                    exception.GetType().Name);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopWhileHeldAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state == LifecycleState.Disposed)
            {
                return;
            }

            await StopWhileHeldAsync(CancellationToken.None).ConfigureAwait(false);
            _state = LifecycleState.Disposed;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public IReadOnlyList<DiscoveredService> GetDiscovered()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            return BuildVisibleSnapshotsLocked(now);
        }
    }

    public async Task IgnoreAsync(ServiceInstanceIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var hash = identity.StableHash;

        // Only hide once the store confirms persistence; if it refused (invalid/at capacity) the
        // suggestion must stay visible rather than silently vanishing for this session only.
        var persisted = await _ignoredStore.IgnoreAsync(hash, cancellationToken).ConfigureAwait(false);
        if (!persisted)
        {
            _logger.LogWarning("Ignore was not persisted; the suggestion remains visible.");
            return;
        }

        bool changed;
        var now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            _ignored.Add(hash);
            changed = RecomputeChangeLocked(now);
            if (changed)
            {
                MarkMaterialChangeLocked(_lifecycleGeneration);
            }
        }
    }

    public async Task ResetIgnoredAsync(CancellationToken cancellationToken = default)
    {
        // ResetAsync repairs the backing file even when the loaded set is already empty.
        await _ignoredStore.ResetAsync(cancellationToken).ConfigureAwait(false);

        bool changed;
        var now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            _ignored.Clear();
            changed = RecomputeChangeLocked(now);
            if (changed)
            {
                MarkMaterialChangeLocked(_lifecycleGeneration);
            }
        }
    }

    private async Task StopWhileHeldAsync(CancellationToken cancellationToken)
    {
        if (_state != LifecycleState.Started)
        {
            return; // idempotent: already stopped or disposed.
        }

        _state = LifecycleState.Stopped;
        EventHandler<DiscoveryObservation>? foundHandler;
        EventHandler<DiscoveryObservation>? updatedHandler;
        EventHandler<DiscoveryObservation>? removedHandler;
        CancellationTokenSource? notificationCts;
        Task notificationTask;
        lock (_sync)
        {
            _acceptBrowserCallbacks = false;
            _lifecycleGeneration++;
            foundHandler = _foundHandler;
            updatedHandler = _updatedHandler;
            removedHandler = _removedHandler;
            _foundHandler = null;
            _updatedHandler = null;
            _removedHandler = null;
            notificationCts = _notificationCts;
            notificationTask = _notificationTask;
            // The notification worker clears and disposes this CTS from a finally block guarded by
            // the same lock. Cancel while still holding the lock so it cannot be disposed between
            // taking the snapshot and requesting cancellation.
            notificationCts?.Cancel();
            _tracked.Clear();
            _published.Clear();
            _materialChangeVersion = 0;
        }

        if (foundHandler is not null)
        {
            _browser.Found -= foundHandler;
        }
        if (updatedHandler is not null)
        {
            _browser.Updated -= updatedHandler;
        }
        if (removedHandler is not null)
        {
            _browser.Removed -= removedHandler;
        }
        _browser.Stop();

        var sweepCts = _sweepCts;
        var sweepLoop = _sweepLoop;
        _sweepCts = null;
        _sweepLoop = Task.CompletedTask;

        sweepCts?.Cancel();
        try
        {
            await Task.WhenAll(sweepLoop, notificationTask)
                .WaitAsync(_options.StopDrainTimeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            _logger.LogDebug("Discovery sweep drained with {Reason}.", exception.GetType().Name);
        }

        sweepCts?.Dispose();

        _logger.LogDebug("Discovery service stopped.");
    }

    private async Task RollbackStartAsync(CancellationTokenSource? sweepCts)
    {
        EventHandler<DiscoveryObservation>? foundHandler;
        EventHandler<DiscoveryObservation>? updatedHandler;
        EventHandler<DiscoveryObservation>? removedHandler;
        CancellationTokenSource? notificationCts;
        Task notificationTask;
        lock (_sync)
        {
            _acceptBrowserCallbacks = false;
            _lifecycleGeneration++;
            foundHandler = _foundHandler;
            updatedHandler = _updatedHandler;
            removedHandler = _removedHandler;
            _foundHandler = null;
            _updatedHandler = null;
            _removedHandler = null;
            notificationCts = _notificationCts;
            notificationTask = _notificationTask;
            notificationCts?.Cancel();
            _tracked.Clear();
            _published.Clear();
            _ignored = new HashSet<string>(StringComparer.Ordinal);
            _materialChangeVersion = 0;
        }

        if (foundHandler is not null)
        {
            _browser.Found -= foundHandler;
        }
        if (updatedHandler is not null)
        {
            _browser.Updated -= updatedHandler;
        }
        if (removedHandler is not null)
        {
            _browser.Removed -= removedHandler;
        }
        try
        {
            _browser.Stop();
        }
        catch (Exception exception)
        {
            _logger.LogDebug("Discovery browser stop during rollback raised {Type}.", exception.GetType().Name);
        }

        sweepCts?.Cancel();
        try
        {
            await notificationTask.WaitAsync(_options.StopDrainTimeout, _timeProvider).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            _logger.LogDebug("Discovery notification rollback drained with {Reason}.", exception.GetType().Name);
        }

        sweepCts?.Dispose();
        _sweepCts = null;
        _sweepLoop = Task.CompletedTask;

        _state = LifecycleState.Stopped;
    }

    private void OnFoundOrUpdated(long generation, DiscoveryObservation observation)
    {
        bool changed;
        lock (_sync)
        {
            if (!IsActiveGenerationLocked(generation))
            {
                return;
            }

            if (!_tracked.TryGetValue(observation.Identity, out var tracked))
            {
                if (!CanAdmitLocked(observation.Identity))
                {
                    _logger.LogDebug("Discovery tracked cap reached; dropping new instance.");
                    return;
                }

                tracked = new TrackedService(observation.Identity, observation.ObservedAt);
                _tracked[observation.Identity] = tracked;
            }

            tracked.Interfaces[observation.InterfaceId] = new InterfaceObservation(
                observation.HostName,
                observation.Port,
                observation.Addresses,
                observation.ObservedAt);

            changed = RecomputeChangeLocked(observation.ObservedAt);
            if (changed)
            {
                MarkMaterialChangeLocked(generation);
            }
        }
    }

    private void OnRemoved(long generation, DiscoveryObservation observation)
    {
        lock (_sync)
        {
            if (IsActiveGenerationLocked(generation)
                && _tracked.TryGetValue(observation.Identity, out var tracked)
                && tracked.Interfaces.TryGetValue(observation.InterfaceId, out var existing)
                && existing.RemoveAfter is null)
            {
                // Grace: keep the suggestion until the timer elapses; the sweep finalizes it.
                tracked.Interfaces[observation.InterfaceId] =
                    existing with { RemoveAfter = observation.ObservedAt + _options.RemovalGrace };
            }
        }
        // No material change yet: still visible during the grace window.
    }

    private async Task SweepLoopAsync(long generation, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_options.SweepInterval, _timeProvider, token).ConfigureAwait(false);
                Sweep(generation);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            _logger.LogError("Discovery sweep loop ended unexpectedly. Exception type: {Type}.", exception.GetType().Name);
        }
    }

    private void Sweep(long generation)
    {
        var now = _timeProvider.GetUtcNow();
        bool changed;
        lock (_sync)
        {
            if (!IsActiveGenerationLocked(generation))
            {
                return;
            }

            foreach (var (identity, tracked) in _tracked.ToList())
            {
                var expired = tracked.Interfaces
                    .Where(pair => IsExpired(pair.Value, now))
                    .Select(pair => pair.Key)
                    .ToList();

                foreach (var interfaceId in expired)
                {
                    tracked.Interfaces.Remove(interfaceId);
                }

                if (tracked.Interfaces.Count == 0)
                {
                    _tracked.Remove(identity);
                }
            }

            changed = RecomputeChangeLocked(now);
            if (changed)
            {
                MarkMaterialChangeLocked(generation);
            }
        }
    }

    private bool IsExpired(InterfaceObservation observation, DateTimeOffset now) =>
        (observation.RemoveAfter is { } removeAfter && now >= removeAfter)
        || now - observation.LastSeenAt >= _options.ExpiryWindow;

    /// <summary>
    /// Rebuilds the visible signature map and reports whether it materially differs from what
    /// was last published. Only host, port, the merged address set and visibility feed the
    /// signature — not last-seen bumps — so ordinary re-announcements do not notify the UI.
    /// </summary>
    private bool RecomputeChangeLocked(DateTimeOffset now)
    {
        var current = new Dictionary<ServiceInstanceIdentity, string>();
        foreach (var snapshot in BuildVisibleSnapshotsLocked(now))
        {
            current[snapshot.Identity] = Signature(snapshot);
        }

        if (current.Count == _published.Count)
        {
            var identical = true;
            foreach (var (identity, signature) in current)
            {
                if (!_published.TryGetValue(identity, out var existing) || existing != signature)
                {
                    identical = false;
                    break;
                }
            }

            if (identical)
            {
                return false;
            }
        }

        _published.Clear();
        foreach (var (identity, signature) in current)
        {
            _published[identity] = signature;
        }

        return true;
    }

    private List<DiscoveredService> BuildVisibleSnapshotsLocked(DateTimeOffset now)
    {
        var snapshots = new List<DiscoveredService>(_tracked.Count);
        foreach (var tracked in _tracked.Values)
        {
            if (_ignored.Contains(tracked.Identity.StableHash))
            {
                continue;
            }

            var live = tracked.Interfaces.Values
                .Where(observation => !IsExpired(observation, now))
                .ToList();
            if (live.Count == 0)
            {
                continue;
            }

            var newest = live.OrderByDescending(observation => observation.LastSeenAt).First();
            var mergedAddresses = DiscoveryInputPolicy.DedupeAddresses(
                live.OrderByDescending(observation => observation.LastSeenAt)
                    .SelectMany(observation => observation.Addresses));

            snapshots.Add(new DiscoveredService
            {
                DiscoveryId = tracked.DiscoveryId,
                Source = DiscoverySource.Mdns,
                Identity = tracked.Identity,
                DisplayName = tracked.Identity.InstanceName,
                HostName = newest.HostName,
                Port = newest.Port,
                Addresses = mergedAddresses,
                FirstSeenAt = tracked.FirstSeenAt,
                LastSeenAt = live.Max(observation => observation.LastSeenAt)
            });
        }

        return snapshots
            .OrderBy(snapshot => snapshot.FirstSeenAt)
            .ThenBy(snapshot => snapshot.DisplayName, StringComparer.Ordinal)
            .Take(DiscoveryInputPolicy.MaxVisibleServices)
            .ToList();
    }

    private static string Signature(DiscoveredService snapshot)
    {
        var addresses = snapshot.Addresses
            .Select(address => address.ToString())
            .OrderBy(text => text, StringComparer.Ordinal);

        // Unit-separator U+001F between fields: cannot occur in a validated host/port/address.
        const char separator = (char)0x1F;
        return string.Join(
            separator,
            snapshot.HostName,
            snapshot.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(',', addresses));
    }

    private bool IsActiveGenerationLocked(long generation) =>
        _acceptBrowserCallbacks && generation == _lifecycleGeneration;

    private bool CanAdmitLocked(ServiceInstanceIdentity identity)
    {
        var ignored = _ignored.Contains(identity.StableHash);
        if (!ignored)
        {
            var visibleTracked = _tracked.Keys.Count(candidate => !_ignored.Contains(candidate.StableHash));
            if (visibleTracked >= DiscoveryInputPolicy.MaxVisibleServices)
            {
                return false;
            }
        }

        if (_tracked.Count < MaxTrackedServices)
        {
            return true;
        }

        if (ignored)
        {
            return false;
        }

        // Preserve the visible-set reserve: a legitimate suggestion may evict the oldest
        // ignored observation, but never another visible suggestion.
        var eviction = _tracked
            .Where(pair => _ignored.Contains(pair.Key.StableHash))
            .OrderBy(pair => pair.Value.FirstSeenAt)
            .ThenBy(pair => pair.Key.StableHash, StringComparer.Ordinal)
            .Select(pair => (ServiceInstanceIdentity?)pair.Key)
            .FirstOrDefault();
        if (eviction is null)
        {
            return false;
        }

        return _tracked.Remove(eviction);
    }

    private void ScheduleChangedLocked(long generation)
    {
        if (!IsActiveGenerationLocked(generation) || _notificationCts is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _notificationCts = cancellation;
        _notificationTask = NotifyChangedAfterDelayAsync(generation, cancellation);
    }

    private void MarkMaterialChangeLocked(long generation)
    {
        if (!IsActiveGenerationLocked(generation))
        {
            return;
        }

        _materialChangeVersion++;
        ScheduleChangedLocked(generation);
    }

    private async Task NotifyChangedAfterDelayAsync(long generation, CancellationTokenSource cancellation)
    {
        long deliveredVersion = 0;
        try
        {
            await Task.Delay(NotificationDelay, _timeProvider, cancellation.Token).ConfigureAwait(false);

            lock (_sync)
            {
                if (!ReferenceEquals(_notificationCts, cancellation)
                    || !IsActiveGenerationLocked(generation))
                {
                    return;
                }

                deliveredVersion = _materialChangeVersion;
            }

            try
            {
                DiscoveredChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "A discovery change subscriber failed. Exception type: {Type}.",
                    exception.GetType().Name);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop, rollback or disposal.
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_notificationCts, cancellation))
                {
                    _notificationCts = null;
                    _notificationTask = Task.CompletedTask;
                    if (IsActiveGenerationLocked(generation)
                        && _materialChangeVersion > deliveredVersion)
                    {
                        ScheduleChangedLocked(generation);
                    }
                }
            }

            cancellation.Dispose();
        }
    }

    private TimeSpan NotificationDelay => _options.ChangeNotificationDelay < MinimumNotificationDelay
        ? MinimumNotificationDelay
        : _options.ChangeNotificationDelay > MaximumNotificationDelay
            ? MaximumNotificationDelay
            : _options.ChangeNotificationDelay;

    private sealed class TrackedService(ServiceInstanceIdentity identity, DateTimeOffset firstSeenAt)
    {
        public ServiceInstanceIdentity Identity { get; } = identity;

        public Guid DiscoveryId { get; } = Guid.NewGuid();

        public DateTimeOffset FirstSeenAt { get; } = firstSeenAt;

        public Dictionary<string, InterfaceObservation> Interfaces { get; } = [];
    }

    private sealed record InterfaceObservation(
        string HostName,
        int Port,
        IReadOnlyList<IPAddress> Addresses,
        DateTimeOffset LastSeenAt)
    {
        public DateTimeOffset? RemoveAfter { get; init; }
    }
}
