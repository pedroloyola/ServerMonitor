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
/// once, on the UI thread, after <see cref="QaOverviewScenario.VanishAfter"/>. Mutations are inert or unsupported, like
/// the other harnesses.
/// </summary>
internal sealed class QaOverviewServerService(QaOverviewScenario scenario, Action<TimeSpan, Action>? schedule = null) : IServerService
{
    private readonly TaskCompletionSource<IReadOnlyList<Server>> _neverCompletes = new();
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
        return Task.FromResult<IReadOnlyList<Server>>(_vanished ? [] : scenario.Servers.Select(entry => entry.Server).ToList());
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

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> HideAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

/// <summary>QA-ONLY <see cref="IServerMetricsStore"/>: the scenario's retained snapshot per server (null = no data).</summary>
internal sealed class QaOverviewMetricsStore(QaOverviewScenario scenario) : IServerMetricsStore
{
    public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) =>
        scenario.Servers.FirstOrDefault(entry => entry.Server.Id == serverId)?.Snapshot;

    public Task<ServerMetricsCollectionResult> RefreshAsync(Server server, CancellationToken cancellationToken = default) =>
        Task.FromResult(ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Unexpected));

    public void Remove(Guid serverId)
    {
        // No-op: QA snapshots are immutable and in-memory.
    }
}
