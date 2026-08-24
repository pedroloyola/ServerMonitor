using System.Collections.Concurrent;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Services;

public sealed class ServerMetricsStore(IServerMetricsCollector collector) : IServerMetricsStore
{
    private readonly ConcurrentDictionary<Guid, ServerMetricsSnapshot> _lastSnapshots = new();
    private readonly ConcurrentDictionary<Guid, Task<ServerMetricsCollectionResult>> _inFlight = new();

    public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => _lastSnapshots.GetValueOrDefault(serverId);

    public Task<ServerMetricsCollectionResult> RefreshAsync(
        Server server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        // Single-flight per ServerId. The in-flight Task is registered (TryAdd)
        // before the collection starts running, so—unlike a GetOrAdd factory that
        // awaits—a synchronously-completing collector can never run its cleanup
        // ahead of insertion and strand a finished Task in the map. A stranded
        // Task would freeze every later refresh on the first result and never
        // re-collect. Different ServerIds use different keys and stay independent.
        while (true)
        {
            if (_inFlight.TryGetValue(server.Id, out var existing))
            {
                return existing;
            }

            var completion = new TaskCompletionSource<ServerMetricsCollectionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inFlight.TryAdd(server.Id, completion.Task))
            {
                _ = CollectAsync(server, completion, cancellationToken);
                return completion.Task;
            }

            // Lost the race to another caller for the same server: our unused
            // completion is discarded and we return the winner's shared Task.
        }
    }

    public void Remove(Guid serverId)
    {
        _lastSnapshots.TryRemove(serverId, out _);
    }

    private async Task CollectAsync(
        Server server,
        TaskCompletionSource<ServerMetricsCollectionResult> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await collector.CollectAsync(server, cancellationToken).ConfigureAwait(false);
            if (result.Snapshot is not null)
            {
                _lastSnapshots[server.Id] = result.Snapshot;
            }

            // Evict our own entry (conditional remove) before completing the Task,
            // so any caller resuming from the awaited Task always observes a cleared
            // slot and a subsequent refresh deterministically starts a fresh run.
            Evict(server.Id, completion.Task);
            completion.SetResult(result);
        }
        catch (OperationCanceledException exception)
        {
            Evict(server.Id, completion.Task);
            completion.SetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            Evict(server.Id, completion.Task);
            completion.SetException(exception);
        }
    }

    private void Evict(Guid serverId, Task<ServerMetricsCollectionResult> completionTask) =>
        _inFlight.TryRemove(
            new KeyValuePair<Guid, Task<ServerMetricsCollectionResult>>(serverId, completionTask));
}
