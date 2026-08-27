using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Services;

/// <summary>
/// Automatic monitoring engine. Each monitored server runs its own async loop (never a
/// dedicated thread); collections are gated by a global concurrency limit so one slow
/// server cannot starve or block another. All time passes through an injected
/// <see cref="TimeProvider"/> so scheduling is deterministically testable. Runs only while
/// the app process is alive; it is not a Windows service.
/// </summary>
public sealed class MonitoringEngine : IMonitoringEngine, IHostedService, IAsyncDisposable
{
    // Only these can produce metrics through the router (Auto resolves via M3 detection).
    private static readonly ServerOperatingSystem[] MonitorableOperatingSystems =
        [ServerOperatingSystem.Auto, ServerOperatingSystem.Linux, ServerOperatingSystem.MacOS];

    private readonly IServerService _serverService;
    private readonly IServerMetricsStore _metricsStore;
    private readonly IServerMonitoringStateStore _stateStore;
    private readonly IMonitoringCycleObserver _cycleObserver;
    private readonly TimeProvider _timeProvider;
    private readonly MonitoringOptions _options;
    private readonly ILogger<MonitoringEngine> _logger;

    private readonly Dictionary<Guid, ServerMonitor> _monitors = [];
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly SemaphoreSlim _concurrencyLimiter;
    private CancellationTokenSource? _engineCts;
    private bool _started;
    private bool _disposed;

    public MonitoringEngine(
        IServerService serverService,
        IServerMetricsStore metricsStore,
        IServerMonitoringStateStore stateStore,
        ILogger<MonitoringEngine> logger,
        TimeProvider? timeProvider = null,
        MonitoringOptions? options = null,
        IMonitoringCycleObserver? cycleObserver = null)
    {
        _serverService = serverService ?? throw new ArgumentNullException(nameof(serverService));
        _metricsStore = metricsStore ?? throw new ArgumentNullException(nameof(metricsStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _cycleObserver = cycleObserver ?? NullMonitoringCycleObserver.Instance;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? MonitoringOptions.Default;
        _concurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentCollections, _options.MaxConcurrentCollections);
    }

    // IHostedService: tie the engine lifecycle to the app host.
    Task IHostedService.StartAsync(CancellationToken cancellationToken) => StartMonitoringAsync(cancellationToken);

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => StopMonitoringAsync(cancellationToken);

    public async Task StartMonitoringAsync(CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started || _disposed)
            {
                return;
            }

            _engineCts = new CancellationTokenSource();
            _started = true;
            _serverService.ServersChanged += OnServersChanged;
            await ReconcileLockedAsync().ConfigureAwait(false);
            _logger.LogInformation("Monitoring engine started with {Count} monitored servers.", _monitors.Count);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    public async Task StopMonitoringAsync(CancellationToken cancellationToken = default)
    {
        List<ServerMonitor> monitors;
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            _serverService.ServersChanged -= OnServersChanged;
            _engineCts?.Cancel();
            monitors = _monitors.Values.ToList();
            _monitors.Clear();
        }
        finally
        {
            _reconcileGate.Release();
        }

        foreach (var monitor in monitors)
        {
            monitor.Cts.Cancel();
        }

        // Wait for loops to unwind, but never block shutdown indefinitely.
        try
        {
            var drain = Task.WhenAll(monitors.Select(monitor => monitor.Loop));
            await drain.WaitAsync(TimeSpan.FromSeconds(5), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            _logger.LogDebug("Monitoring engine stop drained with {Reason}.", exception.GetType().Name);
        }

        foreach (var monitor in monitors)
        {
            monitor.Dispose();
        }

        _engineCts?.Dispose();
        _engineCts = null;
        _logger.LogInformation("Monitoring engine stopped.");
    }

    public async Task<ServerMetricsCollectionResult> RefreshNowAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        ServerMonitor? monitor;
        var request = new TaskCompletionSource<ServerMetricsCollectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Enqueue under the same gate that Stop/Reconcile hold to cancel and remove a
            // monitor, so a manual request can never attach to a loop that is already unwinding:
            // if the monitor were still live here, no cancellation has been ordered yet, so the
            // loop's cancellation drain (RunLoopAsync's finally) is guaranteed to complete this
            // request. A monitor that is gone or already cancelled falls through to a one-off
            // collection instead of orphaning the request (which would hang the caller forever).
            if (_monitors.TryGetValue(serverId, out monitor) && !monitor.Cts.IsCancellationRequested)
            {
                monitor.EnqueueManual(request);
            }
            else
            {
                monitor = null;
            }
        }
        finally
        {
            _reconcileGate.Release();
        }

        if (monitor is not null)
        {
            // Route through the loop so the manual request and any concurrent scheduled
            // cycle converge on a single collection, and the interval restarts from now.
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<ServerMetricsCollectionResult>)state!).TrySetCanceled(),
                request);
            return await request.Task.ConfigureAwait(false);
        }

        // Not monitored (e.g. unsupported OS): a one-off collection so the button still works.
        var servers = await _serverService.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var server = servers.FirstOrDefault(candidate => candidate.Id == serverId);
        if (server is null)
        {
            return ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.InvalidConfiguration);
        }

        return await CollectAndApplyAsync(server, monitor: null, cancellationToken).ConfigureAwait(false);
    }

    private async void OnServersChanged(object? sender, EventArgs args)
    {
        try
        {
            await _reconcileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_started && !_disposed)
                {
                    await ReconcileLockedAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _reconcileGate.Release();
            }
        }
        catch (Exception exception)
        {
            _logger.LogError("Monitoring reconcile failed. Exception type: {Type}.", exception.GetType().Name);
        }
    }

    private async Task ReconcileLockedAsync()
    {
        var servers = await _serverService.GetAllAsync().ConfigureAwait(false);
        var monitored = servers
            .Where(server => MonitorableOperatingSystems.Contains(server.OperatingSystem))
            .ToDictionary(server => server.Id);

        // Remove monitors whose server disappeared or became non-monitorable.
        foreach (var (id, monitor) in _monitors.Where(pair => !monitored.ContainsKey(pair.Key)).ToList())
        {
            monitor.Cts.Cancel();
            _monitors.Remove(id);
            _stateStore.Remove(id);
        }

        var newlyStarted = 0;
        foreach (var server in monitored.Values)
        {
            if (_monitors.TryGetValue(server.Id, out var existing))
            {
                var requiresRecollection = RequiresRecollection(existing.GetServer(), server);
                existing.UpdateServer(server, requiresRecollection);

                continue;
            }

            StartMonitor(server, staggerIndex: newlyStarted++);
        }
    }

    private void StartMonitor(Server server, int staggerIndex)
    {
        var engineToken = _engineCts?.Token ?? CancellationToken.None;
        var monitor = new ServerMonitor
        {
            Server = server,
            Cts = CancellationTokenSource.CreateLinkedTokenSource(engineToken),
            InitialDelay = _options.InitialDelay + TimeSpan.FromTicks(
                _options.StartupStagger.Ticks * Math.Min(staggerIndex, 8))
        };

        _stateStore.Set(ServerMonitoringState.Initial(server.Id));
        _monitors[server.Id] = monitor;
        monitor.Loop = Task.Run(() => RunLoopAsync(monitor), CancellationToken.None);
    }

    private async Task RunLoopAsync(ServerMonitor monitor)
    {
        var token = monitor.Cts.Token;
        try
        {
            await DelayOrWakeAsync(monitor, monitor.InitialDelay, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                // BeginCollection atomically consumes the wake that selected this cycle and marks
                // it in-flight. Manual requests arriving from this point until completion join
                // this exact collection without leaving a stale wake for an immediate duplicate.
                var server = monitor.BeginCollection();
                try
                {
                    var result = await CollectAndApplyAsync(server, monitor, token).ConfigureAwait(false);
                    monitor.CompleteCollection(result);
                }
                catch
                {
                    monitor.CompleteCollection(
                        ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Cancelled));
                    throw;
                }

                var interval = NextInterval(monitor);
                await DelayOrWakeAsync(monitor, interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown / server removed.
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Monitoring loop for {ServerId} ended unexpectedly. Exception type: {Type}.",
                monitor.GetServer().Id,
                exception.GetType().Name);
        }
        finally
        {
            monitor.CompleteCollection(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Cancelled));
        }
    }

    private TimeSpan NextInterval(ServerMonitor monitor)
    {
        var interval = RefreshIntervalPolicy.ToInterval(monitor.GetServer().RefreshIntervalSeconds);
        return monitor.NonRetryableLast && _options.AttentionInterval > interval
            ? _options.AttentionInterval
            : interval;
    }

    private async Task<ServerMetricsCollectionResult> CollectAndApplyAsync(
        Server server,
        ServerMonitor? monitor,
        CancellationToken cancellationToken)
    {
        SetRefreshing(server.Id, true);
        _logger.LogDebug("Collection cycle for {ServerId} starting.", server.Id);
        try
        {
            var result = await RunAttemptsAsync(server, cancellationToken).ConfigureAwait(false);
            ApplyCycleResult(server, monitor, result);
            _logger.LogDebug(
                "Collection cycle for {ServerId} finished ({Outcome}).",
                server.Id,
                MonitoringOutcomeClassifier.Classify(result));
            return result;
        }
        catch (OperationCanceledException)
        {
            SetRefreshing(server.Id, false);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Collection for {ServerId} threw. Exception type: {Type}.",
                server.Id,
                exception.GetType().Name);
            var failure = ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected);
            ApplyCycleResult(server, monitor, failure);
            return failure;
        }
    }

    private async Task<ServerMetricsCollectionResult> RunAttemptsAsync(
        Server server,
        CancellationToken cancellationToken)
    {
        ServerMetricsCollectionResult result;
        var attempt = 0;
        while (true)
        {
            if (attempt > 0)
            {
                var delayIndex = Math.Min(attempt - 1, _options.RetryDelays.Count - 1);
                await Task.Delay(_options.RetryDelays[delayIndex], _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            await _concurrencyLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                result = await _metricsStore.RefreshAsync(server, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _concurrencyLimiter.Release();
            }

            attempt++;
            var outcome = MonitoringOutcomeClassifier.Classify(result);
            if (outcome != MonitoringOutcome.Retryable || attempt >= _options.MaxAttemptsPerCycle)
            {
                return result;
            }
        }
    }

    private void ApplyCycleResult(Server server, ServerMonitor? monitor, ServerMetricsCollectionResult result)
    {
        var now = _timeProvider.GetUtcNow();
        var outcome = MonitoringOutcomeClassifier.Classify(result);
        var previous = _stateStore.Get(server.Id);
        var interval = RefreshIntervalPolicy.ToInterval(server.RefreshIntervalSeconds);

        if (monitor is not null)
        {
            monitor.NonRetryableLast = outcome == MonitoringOutcome.NonRetryable;
        }

        ServerMonitoringState next;
        switch (outcome)
        {
            case MonitoringOutcome.Cancelled:
                next = previous with { IsRefreshing = false };
                break;

            case MonitoringOutcome.Success:
                next = previous with
                {
                    Health = HealthEvaluator.EvaluateFromMetrics(result.Snapshot, _options.Thresholds),
                    IsRefreshing = false,
                    LastAttemptAt = now,
                    LastSuccessAt = now,
                    ConsecutiveFailures = 0,
                    LastError = null,
                    IsStale = false
                };
                break;

            case MonitoringOutcome.Retryable:
                next = previous with
                {
                    Health = ServerHealth.Offline,
                    IsRefreshing = false,
                    LastAttemptAt = now,
                    ConsecutiveFailures = previous.ConsecutiveFailures + 1,
                    LastError = result.ErrorCode,
                    IsStale = StalePolicy.IsStale(previous.LastSuccessAt, now, interval)
                };
                break;

            default: // NonRetryable, NoData -> attention, not offline; keep prior snapshot
                next = previous with
                {
                    Health = ServerHealth.Unknown,
                    IsRefreshing = false,
                    LastAttemptAt = now,
                    ConsecutiveFailures = previous.ConsecutiveFailures + 1,
                    LastError = result.ErrorCode,
                    IsStale = StalePolicy.IsStale(previous.LastSuccessAt, now, interval)
                };
                break;
        }

        _stateStore.Set(next);

        if (previous.Health != next.Health)
        {
            _logger.LogInformation(
                "Server {ServerId} health {From} -> {To}.",
                server.Id,
                previous.Health,
                next.Health);
        }

        // Publish the fresh cycle as a non-blocking side effect (ADR-015 §2/§3). The snapshot is the
        // fresh result's snapshot — null on any non-success — so history never records a stale value.
        // The observer contract is non-throwing, but the engine stays robust to a faulty observer:
        // history must never break monitoring.
        try
        {
            _cycleObserver.OnCycleCompleted(new MonitoringCycleCompletion
            {
                ServerId = server.Id,
                CapturedAtUtc = now,
                Outcome = outcome,
                Health = next.Health,
                Snapshot = result.Snapshot
            });
        }
        catch (Exception exception)
        {
            _logger.LogError("History cycle observer threw. Exception type: {Type}.", exception.GetType().Name);
        }
    }

    private void SetRefreshing(Guid serverId, bool isRefreshing)
    {
        var current = _stateStore.Get(serverId);
        if (current.IsRefreshing != isRefreshing)
        {
            _stateStore.Set(current with { IsRefreshing = isRefreshing });
        }
    }

    private async Task DelayOrWakeAsync(ServerMonitor monitor, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        var wakeTask = monitor.WakeTask;
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(delay, _timeProvider, waitCts.Token);
        await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        waitCts.Cancel(); // stop the pending timer regardless of who won
    }

    private static bool RequiresRecollection(Server current, Server updated) =>
        !string.Equals(current.Host, updated.Host, StringComparison.OrdinalIgnoreCase)
        || current.Port != updated.Port
        || !string.Equals(current.Username, updated.Username, StringComparison.Ordinal)
        || current.OperatingSystem != updated.OperatingSystem
        || current.AuthenticationMethod != updated.AuthenticationMethod
        || !string.Equals(current.PrivateKeyPath, updated.PrivateKeyPath, StringComparison.OrdinalIgnoreCase)
        || current.CredentialReferenceId != updated.CredentialReferenceId
        || current.RefreshIntervalSeconds != updated.RefreshIntervalSeconds;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopMonitoringAsync().ConfigureAwait(false);
        _reconcileGate.Dispose();
        _concurrencyLimiter.Dispose();
    }

    private sealed class ServerMonitor : IDisposable
    {
        private readonly object _sync = new();
        private readonly List<TaskCompletionSource<ServerMetricsCollectionResult>> _pending = [];
        private TaskCompletionSource<bool> _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _isCollecting;

        public required Server Server { get; set; }

        public required CancellationTokenSource Cts { get; init; }

        public Task Loop { get; set; } = Task.CompletedTask;

        public TimeSpan InitialDelay { get; set; }

        public bool NonRetryableLast { get; set; }

        public Task WakeTask
        {
            get { lock (_sync) { return _wake.Task; } }
        }

        public Server GetServer()
        {
            lock (_sync)
            {
                return Server;
            }
        }

        public void UpdateServer(Server server, bool signalWake)
        {
            lock (_sync)
            {
                Server = server;
                if (signalWake)
                {
                    _wake.TrySetResult(true);
                }
            }
        }

        public void EnqueueManual(TaskCompletionSource<ServerMetricsCollectionResult> request)
        {
            lock (_sync)
            {
                _pending.Add(request);
                // A request made while a cycle is already collecting is satisfied by that
                // single-flight result. Only an idle/waiting monitor needs to be awakened.
                if (!_isCollecting)
                {
                    _wake.TrySetResult(true);
                }
            }
        }

        public Server BeginCollection()
        {
            lock (_sync)
            {
                _isCollecting = true;
                if (_wake.Task.IsCompleted)
                {
                    _wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                return Server;
            }
        }

        public void CompleteCollection(ServerMetricsCollectionResult result)
        {
            TaskCompletionSource<ServerMetricsCollectionResult>[] pending;
            lock (_sync)
            {
                pending = [.. _pending];
                _pending.Clear();
                _isCollecting = false;
            }

            foreach (var request in pending)
            {
                request.TrySetResult(result);
            }
        }

        public void Dispose() => Cts.Dispose();
    }
}
