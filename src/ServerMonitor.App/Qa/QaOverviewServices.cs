using System.Collections.Concurrent;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY read-only <see cref="IServerService"/> for --qa-overview. Serves the scenario's servers (hidden ones included,
/// so the Servidores note is real) and never persists anything. The <c>loading</c> scenario's load never completes, so
/// the loading state can be inspected for as long as needed. The <c>vanishing</c> scenario empties the list (in memory)
/// once, on the UI thread, after <see cref="QaOverviewScenario.VanishAfter"/>. Add/edit are unsupported. Hide / restore /
/// remove are inert (false) in the UI.4 scenarios; in a <see cref="QaOverviewScenario.Mutable"/> scenario (UI.5) they
/// really change the IN-MEMORY list and announce it - or return false when the scenario asks them to fail. Nothing is
/// ever persisted.
/// </summary>
internal sealed class QaOverviewServerService(QaOverviewScenario scenario, Action<TimeSpan, Action>? schedule = null) : IServerService
{
    private readonly TaskCompletionSource<IReadOnlyList<Server>> _neverCompletes = new();
    private readonly Lock _gate = new();
    private readonly List<Server> _servers = scenario.Servers.Select(entry => entry.Server).ToList();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _vanishTimer;
    private bool _vanishScheduled;
    private bool _vanished;

    public event EventHandler? ServersChanged;

    public Task<IReadOnlyList<Server>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (scenario.NeverLoads)
        {
            return _neverCompletes.Task;
        }

        StartVanishTimer();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Server>>(_vanished ? [] : _servers.ToList());
        }
    }

    /// <summary>Every server goes away (in memory) and the change is announced, as a real removal would.</summary>
    internal void Vanish()
    {
        _vanished = true;
        ServersChanged?.Invoke(this, EventArgs.Empty);
    }

    // Created on the UI thread by the first load, so the change reaches the view models on the thread a real one would.
    private void StartVanishTimer()
    {
        if (scenario.VanishAfter is not { } delay || _vanishScheduled || _vanished)
        {
            return;
        }

        _vanishScheduled = true;
        (schedule ?? ScheduleOnUiThread)(delay, Vanish);
    }

    private void ScheduleOnUiThread(TimeSpan delay, Action vanish)
    {
        if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is not { } queue)
        {
            return;
        }

        _vanishTimer = queue.CreateTimer();
        _vanishTimer.Interval = delay;
        _vanishTimer.IsRepeating = false;
        _vanishTimer.Tick += (_, _) => vanish();
        _vanishTimer.Start();
    }

    public Task<ServerOperationResult> AddAsync(ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA overview harness is read-only.");

    public Task<ServerOperationResult> AddAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA overview harness is read-only.");

    public Task<ServerOperationResult> UpdateAsync(Guid id, ServerInput input, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("QA overview harness is read-only.");

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Mutate(servers => servers.RemoveAll(server => server.Id == id) > 0));

    public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Mutate(servers => SetHidden(servers, id, hidden: true)));

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Mutate(servers => SetHidden(servers, id, hidden: false)));

    /// <summary>The in-memory list as it is now (tests).</summary>
    internal IReadOnlyList<Server> Current
    {
        get
        {
            lock (_gate)
            {
                return _servers.ToList();
            }
        }
    }

    private bool Mutate(Func<List<Server>, bool> change)
    {
        if (!scenario.Mutable || !scenario.OperationsSucceed)
        {
            return false;
        }

        bool changed;
        lock (_gate)
        {
            changed = change(_servers);
        }

        // Announced outside the lock, as the real service does after its save.
        if (changed)
        {
            ServersChanged?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    private static bool SetHidden(List<Server> servers, Guid id, bool hidden)
    {
        var index = servers.FindIndex(server => server.Id == id);
        if (index < 0 || servers[index].IsHidden == hidden)
        {
            return false;
        }

        servers[index] = servers[index] with { IsHidden = hidden };
        return true;
    }
}

/// <summary>
/// QA-ONLY <see cref="IServerMetricsStore"/>: the scenario's retained snapshot per server (null = no data). In memory; a
/// successful UI.5 refresh (<see cref="QaOverviewMonitoringEngine"/>) replaces a server's snapshot with a fresh copy.
/// </summary>
internal sealed class QaOverviewMetricsStore(QaOverviewScenario scenario) : IServerMetricsStore
{
    private readonly ConcurrentDictionary<Guid, ServerMetricsSnapshot> _snapshots = new(
        scenario.Servers.Where(entry => entry.Snapshot is not null)
            .Select(entry => KeyValuePair.Create(entry.Server.Id, entry.Snapshot!)));

    public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => _snapshots.GetValueOrDefault(serverId);

    internal void Replace(ServerMetricsSnapshot snapshot) => _snapshots[snapshot.ServerId] = snapshot;

    public Task<ServerMetricsCollectionResult> RefreshAsync(Server server, CancellationToken cancellationToken = default) =>
        Task.FromResult(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected));

    public void Remove(Guid serverId)
    {
        // No-op: QA snapshots are in-memory and keyed by the synthetic id; a removed server is simply never asked for.
    }
}

/// <summary>
/// QA-ONLY <see cref="IMonitoringEngine"/> for --qa-overview. Never schedules, connects or collects. A manual refresh in a
/// UI.5 <see cref="QaOverviewScenario.Mutable"/> scenario that succeeds produces a FRESH reading at the catalogue's fixed
/// clock (same synthetic values, health from the engine's own rule) and publishes it through the real state store, as a
/// cycle would; in every other scenario it fails with <see cref="MetricsCollectionErrorCode.Unexpected"/> and changes
/// nothing (the UI.4 behaviour).
/// </summary>
internal sealed class QaOverviewMonitoringEngine(
    QaOverviewScenario scenario,
    QaOverviewMetricsStore metrics,
    IServerMonitoringStateStore states) : IMonitoringEngine
{
    public Task StartMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopMonitoringAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ServerMetricsCollectionResult> RefreshNowAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        if (!scenario.Mutable || !scenario.OperationsSucceed || metrics.GetLastSnapshot(serverId) is not { } previous)
        {
            return Task.FromResult(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected));
        }

        var fresh = previous with { CollectedAt = QaOverviewCatalog.Now };
        metrics.Replace(fresh);
        states.Set(states.Get(serverId) with
        {
            Health = ServerMonitor.Core.Monitoring.HealthEvaluator.EvaluateFromMetrics(fresh),
            IsRefreshing = false,
            IsStale = false,
            LastAttemptAt = QaOverviewCatalog.Now,
            LastSuccessAt = QaOverviewCatalog.Now,
            ConsecutiveFailures = 0,
            LastError = null
        });
        return Task.FromResult(ServerMetricsCollectionResult.Success(
            fresh,
            new SshConnectionResult { State = ServerConnectionState.Connected }));
    }
}
