using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Services;

/// <summary>
/// Turns workload requests into read-only collections, off the engine thread (M11). Scheduled requests
/// arrive from the cadence observer via the bounded queue; manual refreshes enlist directly. Both share
/// one per-server single-flight slot (§37): concurrent scheduled+manual requests for a server coalesce
/// onto one in-flight collection whose result satisfies every waiter. Its own global limiter bounds
/// cross-server concurrency so workloads never contend with the host engine (§36). Freshness carry-over
/// on failure is applied here via <see cref="WorkloadFreshnessMerger"/> (§39). Implements
/// <see cref="IWorkloadRefreshCoordinator"/> so a manual/Refresh-All refresh forces and awaits a
/// collection. A collection failure never affects the host monitoring engine (§38).
/// </summary>
public sealed class WorkloadCollectorService : IHostedService, IWorkloadRefreshCoordinator
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    private readonly WorkloadRequestQueue _queue;
    private readonly IWorkloadCollector _collector;
    private readonly IServerWorkloadStore _store;
    private readonly IServerService _serverService;
    private readonly WorkloadOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkloadCollectorService> _logger;

    private readonly SemaphoreSlim _limiter;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ServerSlot> _slots = [];

    // In-flight collection tasks, tracked so shutdown can drain them before the CTS is disposed
    // (H-02). Guarded by _sync.
    private readonly List<Task> _inFlight = [];

    private CancellationTokenSource? _cts;
    private Task _consumeTask = Task.CompletedTask;
    private bool _stopped;

    public WorkloadCollectorService(
        WorkloadRequestQueue queue,
        IWorkloadCollector collector,
        IServerWorkloadStore store,
        IServerService serverService,
        ILogger<WorkloadCollectorService> logger,
        WorkloadOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _serverService = serverService ?? throw new ArgumentNullException(nameof(serverService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? WorkloadOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _limiter = new SemaphoreSlim(_options.MaxConcurrentCollections, _options.MaxConcurrentCollections);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _serverService.ServersChanged += OnServersChanged;
        _consumeTask = Task.Run(() => ConsumeAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _serverService.ServersChanged -= OnServersChanged;

        // Mark stopped and snapshot the in-flight collections under the same lock that admits new starts.
        // After this point no new collection can start (RefreshNowAsync/DispatchScheduled see _stopped),
        // so this snapshot is the complete set to drain before disposing the CTS (H-02).
        Task[] inFlight;
        lock (_sync)
        {
            _stopped = true;
            inFlight = [.. _inFlight];
        }

        // Stop accepting scheduled requests and let the consumer exit once the queue drains.
        _queue.Complete();
        try
        {
            await _consumeTask.WaitAsync(DrainTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            _logger.LogDebug("Workload collector drain ended with {Reason}.", exception.GetType().Name);
        }

        // Cancel the in-flight collections, then wait for them to unwind BEFORE disposing the CTS, so no
        // collection can ever observe a disposed/null token (H-02). Bounded so shutdown never hangs.
        _cts?.Cancel();
        try
        {
            await Task.WhenAll(inFlight).WaitAsync(DrainTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            _logger.LogDebug("Workload collection drain ended with {Reason}.", exception.GetType().Name);
        }

        // Fail any still-pending waiters so no manual refresh hangs across shutdown.
        FailAllWaiters();

        _cts?.Dispose();
        _cts = null;
    }

    public async Task RefreshNowAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<ServerWorkloadSnapshot?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_sync)
        {
            if (_stopped)
            {
                // Shutting down: nothing will collect. Complete as a no-op rather than hanging.
                completion.TrySetResult(null);
                return;
            }

            // Enlist the waiter and, if this request owns the collection, start it — all under the same
            // lock that clears in-flight on completion and that shutdown holds to stop. This makes the
            // start decision, token capture and stop mutually exclusive, so a collection can never begin
            // after shutdown has disposed the CTS (H-02), and a manual refresh deterministically joins any
            // in-flight collection (P-007/L-010), exactly like the M6 engine.
            var (start, slot) = EnlistLocked(serverId, completion);
            if (start)
            {
                StartCollectionLocked(serverId, slot);
            }
        }

        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<ServerWorkloadSnapshot?>)state!).TrySetCanceled(),
            completion);
        await completion.Task.ConfigureAwait(false);
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var request))
                {
                    DispatchScheduled(request.ServerId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown after the bounded drain window.
        }
        catch (Exception exception)
        {
            _logger.LogError("Workload collector loop ended unexpectedly. Type: {Type}.", exception.GetType().Name);
        }
    }

    private void DispatchScheduled(Guid serverId)
    {
        lock (_sync)
        {
            if (_stopped)
            {
                return;
            }

            // Scheduled requests carry no waiter; they only ensure a collection is (or becomes) in flight.
            var (start, slot) = EnlistLocked(serverId, waiter: null);
            if (start)
            {
                StartCollectionLocked(serverId, slot);
            }
        }
    }

    private (bool start, ServerSlot slot) EnlistLocked(
        Guid serverId,
        TaskCompletionSource<ServerWorkloadSnapshot?>? waiter)
    {
        if (!_slots.TryGetValue(serverId, out var slot))
        {
            slot = new ServerSlot();
            _slots[serverId] = slot;
        }

        if (waiter is not null)
        {
            slot.Waiters.Add(waiter);
        }

        // Single-flight: only an idle slot starts a collection; otherwise this request coalesces.
        var start = !slot.InFlight;
        if (start)
        {
            slot.InFlight = true;
        }

        return (start, slot);
    }

    // Called under _sync. Captures the token from the live CTS (which shutdown cannot dispose until this
    // task, now tracked in _inFlight, has drained) and launches the collection off the caller/consumer
    // stack so a slow SSH session blocks neither the UI nor the queue reader.
    private void StartCollectionLocked(Guid serverId, ServerSlot slot)
    {
        var token = _cts?.Token ?? CancellationToken.None;
        var task = Task.Run(() => RunCollectionAsync(serverId, slot, token), CancellationToken.None);
        _inFlight.Add(task);
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var self = (WorkloadCollectorService)state!;
                lock (self._sync)
                {
                    self._inFlight.Remove(completed);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private async Task RunCollectionAsync(Guid serverId, ServerSlot slot, CancellationToken cancellationToken)
    {
        ServerWorkloadSnapshot? result = null;
        try
        {
            var server = await ResolveServerAsync(serverId, cancellationToken).ConfigureAwait(false);
            if (server is null)
            {
                // Server removed/unknown: drop any stale store entry; waiters complete with null.
                _store.Remove(serverId);
                return;
            }

            await _limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            ServerWorkloadSnapshot attempt;
            try
            {
                attempt = await _collector.CollectAsync(server, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _limiter.Release();
            }

            var now = _timeProvider.GetUtcNow();
            var previous = _store.Get(serverId);
            result = WorkloadFreshnessMerger.Merge(previous, attempt, now);
            _store.Set(result);
        }
        catch (OperationCanceledException)
        {
            // Shutdown/cancelled: leave the prior snapshot intact; waiters get the last known value.
            result = _store.Get(serverId);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Workload collection for {ServerId} threw. Type: {Type}.",
                serverId,
                exception.GetType().Name);
            result = _store.Get(serverId);
        }
        finally
        {
            CompleteSlot(serverId, slot, result);
        }
    }

    private void CompleteSlot(Guid serverId, ServerSlot slot, ServerWorkloadSnapshot? result)
    {
        TaskCompletionSource<ServerWorkloadSnapshot?>[] waiters;
        lock (_sync)
        {
            waiters = [.. slot.Waiters];
            slot.Waiters.Clear();
            slot.InFlight = false;
            if (slot.Waiters.Count == 0)
            {
                // Nothing pending and not in flight: forget the slot so the dictionary stays bounded.
                _slots.Remove(serverId);
            }
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(result);
        }
    }

    private async Task<Server?> ResolveServerAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var servers = await _serverService.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return servers.FirstOrDefault(candidate => candidate.Id == serverId);
    }

    private async void OnServersChanged(object? sender, EventArgs args)
    {
        try
        {
            var servers = await _serverService.GetAllAsync().ConfigureAwait(false);
            var present = servers.Select(server => server.Id).ToHashSet();

            // Derive removals from the store itself, so this is correct from the very first change after
            // startup (no warm-up set to seed) and idempotent: any stored snapshot whose server no longer
            // exists is dropped (M-01; mirrors the M6 reconcile, honors §73/ADR-016).
            foreach (var snapshot in _store.GetAll())
            {
                if (!present.Contains(snapshot.ServerId))
                {
                    _store.Remove(snapshot.ServerId);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogError("Workload reconcile failed. Type: {Type}.", exception.GetType().Name);
        }
    }

    private void FailAllWaiters()
    {
        List<TaskCompletionSource<ServerWorkloadSnapshot?>> waiters = [];
        lock (_sync)
        {
            foreach (var slot in _slots.Values)
            {
                waiters.AddRange(slot.Waiters);
                slot.Waiters.Clear();
            }
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(null);
        }
    }

    private sealed class ServerSlot
    {
        public bool InFlight { get; set; }

        public List<TaskCompletionSource<ServerWorkloadSnapshot?>> Waiters { get; } = [];
    }
}
