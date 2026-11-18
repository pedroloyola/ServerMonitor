using System.Collections.Concurrent;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Probes;

namespace ServerMonitor.App.Services;

public sealed class ServerMetricsStore(IServerMetricsCollector collector) : IServerMetricsStore
{
    private readonly ConcurrentDictionary<Guid, ServerMetricsSnapshot> _lastSnapshots = new();
    private readonly ConcurrentDictionary<Guid, Task<ServerMetricsCollectionResult>> _inFlight = new();

    public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => _lastSnapshots.GetValueOrDefault(serverId);

    public Task<ServerMetricsCollectionResult> RefreshAsync(
        Server server,
        CancellationToken cancellationToken = default) =>
        RefreshAsync(server, null, cancellationToken);

    public Task<ServerMetricsCollectionResult> RefreshAsync(
        Server server,
        RemoteProbeReservation? probes,
        CancellationToken cancellationToken = default)
    {
        if (server is null)
        {
            // The reservation is ours from here on: close it, or the server's second phase would stay reserved forever.
            probes?.Complete();
            throw new ArgumentNullException(nameof(server));
        }

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
                // Joining a refresh already under way: its session runs no probes of ours.
                probes?.Complete();
                return existing;
            }

            var completion = new TaskCompletionSource<ServerMetricsCollectionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inFlight.TryAdd(server.Id, completion.Task))
            {
                _ = CollectAsync(server, probes, completion, cancellationToken);
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
        RemoteProbeReservation? probes,
        TaskCompletionSource<ServerMetricsCollectionResult> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            // The built-in result is published from the collector's callback, so the refresh completes while the
            // probes still run; the result the collector returns afterwards is then the same one and is ignored.
            var phases = new MetricsCollectionPhases(result => Publish(server.Id, completion, result), probes);
            var result = await collector.CollectAsync(server, phases, cancellationToken).ConfigureAwait(false);
            Publish(server.Id, completion, result);
        }
        catch (OperationCanceledException exception)
        {
            Evict(server.Id, completion.Task);
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            Evict(server.Id, completion.Task);
            completion.TrySetException(exception);
        }
        finally
        {
            probes?.Complete();
        }
    }

    private void Publish(
        Guid serverId,
        TaskCompletionSource<ServerMetricsCollectionResult> completion,
        ServerMetricsCollectionResult result)
    {
        if (completion.Task.IsCompleted)
        {
            return;
        }

        if (result.Snapshot is not null)
        {
            _lastSnapshots[serverId] = result.Snapshot;
        }

        // Evict our own entry (conditional remove) before completing the Task,
        // so any caller resuming from the awaited Task always observes a cleared
        // slot and a subsequent refresh deterministically starts a fresh run.
        Evict(serverId, completion.Task);
        completion.TrySetResult(result);
    }

    private void Evict(Guid serverId, Task<ServerMetricsCollectionResult> completionTask) =>
        _inFlight.TryRemove(
            new KeyValuePair<Guid, Task<ServerMetricsCollectionResult>>(serverId, completionTask));
}
