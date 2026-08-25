using ServerMonitor.App.Services;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// <see cref="IServerMetricsStore"/> that returns scripted results for the monitoring engine.
/// Each <see cref="RefreshAsync"/> call is answered by <see cref="ResultFactory"/>, which
/// receives the target server and the zero-based call index so a test can, for example, fail
/// the first attempt and succeed on the retry. Every call is counted for single-flight and
/// retry assertions.
/// </summary>
internal sealed class ScriptedMetricsStore : IServerMetricsStore
{
    private int _callCount;

    /// <summary>(server, zero-based call index) → result. Defaults to a healthy snapshot.</summary>
    public Func<Server, int, ServerMetricsCollectionResult> ResultFactory { get; set; } =
        (server, _) => TestData.Success(TestData.Snapshot(server.Id, cpu: 10, memoryPercent: 20, diskPercent: 30));

    public int CallCount => Volatile.Read(ref _callCount);

    public int RemoveCount { get; private set; }

    public ServerMetricsSnapshot? GetLastSnapshot(Guid serverId) => null;

    public Task<ServerMetricsCollectionResult> RefreshAsync(
        Server server,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = Interlocked.Increment(ref _callCount) - 1;
        return Task.FromResult(ResultFactory(server, index));
    }

    public void Remove(Guid serverId) => RemoveCount++;
}
