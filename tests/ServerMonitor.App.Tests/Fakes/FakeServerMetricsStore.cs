using ServerMonitor.App.Services;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// Controllable <see cref="IServerMetricsStore"/> used to isolate
/// <c>ServerCardViewModel</c> from the real store. <see cref="Gate"/> holds a
/// refresh open so the ViewModel's in-progress state can be observed
/// deterministically.
/// </summary>
internal sealed class FakeServerMetricsStore : IServerMetricsStore
{
    private TaskCompletionSource<ServerMetricsCollectionResult>? _gate;

    public ServerMetricsSnapshot? InitialSnapshot { get; set; }

    public ServerMetricsCollectionResult? NextResult { get; set; }

    public int RefreshCount { get; private set; }

    public Server? LastServer { get; private set; }

    public int RemoveCount { get; private set; }

    public TaskCompletionSource<ServerMetricsCollectionResult> Gate()
    {
        _gate = new TaskCompletionSource<ServerMetricsCollectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _gate;
    }

    public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => InitialSnapshot;

    public Task<ServerMetricsCollectionResult> RefreshAsync(
        Server server,
        CancellationToken cancellationToken = default)
    {
        RefreshCount++;
        LastServer = server;

        if (_gate is not null)
        {
            return _gate.Task;
        }

        return Task.FromResult(
            NextResult ?? throw new InvalidOperationException("No result configured on FakeServerMetricsStore."));
    }

    public void Remove(Guid serverId) => RemoveCount++;
}
