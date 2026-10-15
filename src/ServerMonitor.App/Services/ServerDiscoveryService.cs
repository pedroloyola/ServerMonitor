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
/// tracks first/last seen, applies a short grace to removals (and an expiry backstop longer than a
/// search, see <see cref="DiscoveryOptions.ExpiryWindow"/>) —
/// all through an injected <see cref="TimeProvider"/>. It raises <see cref="DiscoveredChanged"/> only
/// on material changes, and never touches SSH, credentials, trust or metrics.
/// <para>
/// REL.0B (UI9-FW-PROMPT): it is still registered as an <see cref="IHostedService"/>, but only so the host
/// STOPS it on exit, in the documented reverse order. The host's start is deliberately a no-op: the browser
/// binds UDP listeners on 0.0.0.0:5353 / [::]:5353, which is what raises the Windows Firewall prompt, so it
/// starts only through <see cref="StartSearchAsync"/> from an explicit user action - never at launch and never
/// in <c>--background</c>.
/// </para>
/// </summary>
/// <remarks>
/// The lifecycle (start/stop/dispose) is serialized behind a dedicated async gate and a small
/// state machine, so a start and a stop cannot interleave, a failed or cancelled start rolls back
/// cleanly (and never poisons a later start), stop is idempotent and drain-safe, and dispose
/// fences any future start. The store mutations use a separate monitor lock.
/// </remarks>
public sealed class ServerDiscoveryService : IServerDiscoveryService, IServerDiscoveryActivity, IHostedService, IAsyncDisposable
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
    private volatile bool _isSearching;
    private DateTimeOffset _searchStartedAt;
    // N-r2-2: the identities that were USABLE at some point in THIS search (reset on start), so "nothing found" is never
    // claimed for a search that showed a result which later expired.
    // REL.0D O-7: "usable" is what the search's starter says the user could still add (_isUsable). A search whose only
    // announcers are servers already configured, ignored devices (also ignored during the search) or duplicates of
    // those ends at the 40 s bound as NothingFound instead of listening, with nothing to show, until the 10 min limit.
    // Bounded like the store itself; once full, more identities add nothing to the decision.
    private readonly HashSet<ServiceInstanceIdentity> _usableSeen = [];
    private Func<DiscoveredService, bool>? _isUsable;
    // Vigil NIT-A / Cortex addendum: one automatic stop at a time; cleared when it fails so the next sweep retries.
    private int _autoStopInFlight;
    // Cortex addendum: the browser's own stop threw. Guarded by the lifecycle gate; every later stop/start/dispose and a
    // bounded retry loop try again, so a failed Stop() never leaves the 5353 listener open with the service "stopped".
    private bool _browserStopPending;
    private Task _browserStopRetry = Task.CompletedTask;
    internal const int MaxBrowserStopRetries = 600;
    private volatile DiscoverySearchEnd _lastSearchEnd = DiscoverySearchEnd.None;

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

    /// <inheritdoc />
    public bool IsSearching => _isSearching;

    /// <inheritdoc />
    public event EventHandler? IsSearchingChanged;

    /// <inheritdoc />
    public DiscoverySearchEnd LastSearchEnd => _lastSearchEnd;

    /// <summary>The host never starts discovery (REL.0B); see the type remarks.</summary>
    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    /// <inheritdoc />
    public Task StartSearchAsync(CancellationToken cancellationToken = default) => StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task StartSearchAsync(Func<DiscoveredService, bool> isUsable, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isUsable);
        return StartCoreAsync(isUsable, cancellationToken);
    }

    /// <inheritdoc />
    public Task StopSearchAsync(CancellationToken cancellationToken = default) => StopAsync(cancellationToken);

    /// <summary>Starts a search in which every visible suggestion counts as usable (the pre-O-7 behaviour).</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => StartCoreAsync(null, cancellationToken);

    private async Task StartCoreAsync(Func<DiscoveredService, bool>? isUsable, CancellationToken cancellationToken)
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
                    _searchStartedAt = _timeProvider.GetUtcNow();
                    _usableSeen.Clear();
                    _isUsable = isUsable;
                    Volatile.Write(ref _autoStopInFlight, 0);
                    _acceptBrowserCallbacks = true;
                    _foundHandler = (_, observation) => OnFoundOrUpdated(generation, observation);
                    _updatedHandler = (_, observation) => OnFoundOrUpdated(generation, observation);
                    _removedHandler = (_, observation) => OnRemoved(generation, observation);
                }

                // A previous session whose stop failed is closed before a new one opens (never two listeners).
                if (_browserStopPending && !TryStopBrowser())
                {
                    throw new InvalidOperationException("The previous discovery session could not be stopped.");
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
                _lastSearchEnd = DiscoverySearchEnd.None;
                SetSearching(true);
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

                // REL.0B (Cortex N-3): a failed user-started search is reported, not silently reset.
                _lastSearchEnd = DiscoverySearchEnd.Failed;
                RaiseActivityChanged();
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

    private async Task StopWhileHeldAsync(CancellationToken cancellationToken, DiscoverySearchEnd end = DiscoverySearchEnd.Stopped)
    {
        if (_state != LifecycleState.Started)
        {
            // Idempotent: already stopped or disposed - but a browser whose stop failed earlier is retried here.
            if (_browserStopPending)
            {
                TryStopBrowser();
            }

            return;
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
            _isUsable = null;
            _usableSeen.Clear();
        }

        // REL.0B (Cortex C-1): signal only AFTER the store is empty. IsSearchingChanged is raised synchronously and a
        // subscriber on the same thread (the hide path runs on the UI thread) rebuilds from GetDiscovered(); signalling
        // first showed the stopped search's suggestions with no listener and hid the search action. No
        // DiscoveredChanged follows a stop (the notification worker was just cancelled), so this is the one signal.
        _lastSearchEnd = end;
        SetSearching(false);

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

        // The service-side state is already final (store cleared, callbacks fenced by the generation, UI told). The
        // listener itself is closed by the browser; if that throws, it is retried until it succeeds (never assumed).
        if (!TryStopBrowser())
        {
            StartBrowserStopRetry();
        }

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
            _isUsable = null;
            _usableSeen.Clear();
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
        if (!TryStopBrowser())
        {
            StartBrowserStopRetry();
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
        SetSearching(false);
    }

    // Called only while the lifecycle gate is held. A subscriber that throws must never break start/stop.
    private void SetSearching(bool value)
    {
        if (_isSearching == value)
        {
            return;
        }

        _isSearching = value;
        RaiseActivityChanged();
    }

    /// <summary>Called only while the lifecycle gate is held. True when the browser is known to be stopped.</summary>
    private bool TryStopBrowser()
    {
        try
        {
            _browser.Stop();
            _browserStopPending = false;
            return true;
        }
        catch (Exception exception)
        {
            _browserStopPending = true;
            _logger.LogWarning("Discovery browser stop failed; it will be retried. Exception type: {Type}.", exception.GetType().Name);
            return false;
        }
    }

    /// <summary>Called only while the lifecycle gate is held. One bounded retry loop at a time, on the injected clock.</summary>
    private void StartBrowserStopRetry()
    {
        if (!_browserStopRetry.IsCompleted)
        {
            return;
        }

        _browserStopRetry = Task.Run(BrowserStopRetryLoopAsync, CancellationToken.None);
    }

    private async Task BrowserStopRetryLoopAsync()
    {
        for (var attempt = 0; attempt < MaxBrowserStopRetries; attempt++)
        {
            await Task.Delay(_options.SweepInterval, _timeProvider).ConfigureAwait(false);
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Settled meanwhile (a later stop, start or dispose retried it), or settled now.
                if (!_browserStopPending || TryStopBrowser())
                {
                    return;
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        _logger.LogError("Discovery browser stop kept failing; giving up after {Attempts} retries.", MaxBrowserStopRetries);
    }

    private void RaiseActivityChanged()
    {
        try
        {
            IsSearchingChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _logger.LogDebug("Discovery activity subscriber raised {Type}.", exception.GetType().Name);
        }
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
                if (SearchLimitReached(generation) is { } end && Interlocked.Exchange(ref _autoStopInFlight, 1) == 0)
                {
                    // Not awaited: the stop drains THIS loop. The loop keeps ticking until that stop cancels it, so a
                    // failed automatic stop is retried on a later tick instead of leaving the search unbounded.
                    _ = Task.Run(() => AutoStopAsync(generation, end), CancellationToken.None);
                }
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

    /// <summary>REL.0B (Vigil L-1, Prism N-5): a user-started search is bounded; see <see cref="DiscoveryOptions"/>.</summary>
    private DiscoverySearchEnd? SearchLimitReached(long generation)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            if (!IsActiveGenerationLocked(generation))
            {
                return null;
            }

            var elapsed = now - _searchStartedAt;
            if (elapsed >= _options.MaxSearchDuration)
            {
                return DiscoverySearchEnd.TimeLimit;
            }

            // No re-judging here: the sweep that precedes this check on every tick recomputes the visible set, so a
            // suggestion the starter could not judge yet (its configured servers were still being read) is judged again.
            if (elapsed >= _options.NothingFoundTimeout && !HasUsableResultLocked())
            {
                return DiscoverySearchEnd.NothingFound;
            }

            return null;
        }
    }

    private async Task AutoStopAsync(long generation, DiscoverySearchEnd end)
    {
        try
        {
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                bool current;
                lock (_sync)
                {
                    current = IsActiveGenerationLocked(generation);
                }

                // A manual stop, a newer start or dispose already won: never stop a search this loop did not own.
                if (current && _state == LifecycleState.Started)
                {
                    await StopWhileHeldAsync(CancellationToken.None, end).ConfigureAwait(false);
                    _logger.LogDebug("Discovery search ended by itself ({End}).", end);
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        catch (Exception exception)
        {
            _logger.LogError("Discovery automatic stop failed; the next sweep retries. Exception type: {Type}.", exception.GetType().Name);
            Volatile.Write(ref _autoStopInFlight, 0);
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
        var visible = BuildVisibleSnapshotsLocked(now);
        foreach (var snapshot in visible)
        {
            current[snapshot.Identity] = Signature(snapshot);
        }

        RecordUsableLocked(visible);

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

    /// <summary>Records which of <paramref name="visible"/> are usable (once usable, an identity stays recorded).</summary>
    private void RecordUsableLocked(List<DiscoveredService> visible)
    {
        foreach (var snapshot in visible)
        {
            if (_usableSeen.Count >= MaxTrackedServices)
            {
                break;
            }

            if (!_usableSeen.Contains(snapshot.Identity) && IsUsableLocked(snapshot))
            {
                _usableSeen.Add(snapshot.Identity);
            }
        }
    }

    /// <summary>
    /// Whether this search had a usable result the user has not ignored since (one that later expired still counts,
    /// N-r2-2).
    /// </summary>
    private bool HasUsableResultLocked()
    {
        foreach (var identity in _usableSeen)
        {
            if (!_ignored.Contains(identity.StableHash))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsUsableLocked(DiscoveredService snapshot)
    {
        if (_isUsable is not { } isUsable)
        {
            return true;
        }

        try
        {
            return isUsable(snapshot);
        }
        catch (Exception exception)
        {
            // Fail toward the pre-O-7 behaviour: a suggestion that cannot be judged keeps the search to its 10 min bound.
            _logger.LogDebug("Discovery usable check raised {Type}; counted as usable.", exception.GetType().Name);
            return true;
        }
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
