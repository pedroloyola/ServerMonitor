using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// Covers the transient metrics cache and its per-server single-flight.
/// Several cases (Twice, AfterCompletion, FailureAfterEarlierSuccess) also act
/// as regression tests: the fake collector completes synchronously, which used
/// to leave a completed task stranded in the in-flight map so later refreshes
/// returned the first stale result and never re-ran the collector.
/// </summary>
public sealed class ServerMetricsStoreTests
{
    [Fact]
    public void GetLastSnapshot_UnknownServer_ReturnsNull()
    {
        var store = new ServerMetricsStore(new FakeMetricsCollector());

        Assert.Null(store.GetLastSnapshot(Guid.NewGuid()));
    }

    [Fact]
    public async Task RefreshAsync_Success_StoresSnapshotRetrievableById()
    {
        var server = TestData.LinuxServer();
        var snapshot = TestData.Snapshot(server.Id, cpu: 12);
        var collector = new FakeMetricsCollector { Result = TestData.Success(snapshot) };
        var store = new ServerMetricsStore(collector);

        var result = await store.RefreshAsync(server);

        Assert.True(result.IsSuccess);
        Assert.Same(snapshot, store.GetLastSnapshot(server.Id));
    }

    [Fact]
    public async Task RefreshAsync_Twice_UpdatesStoredSnapshot()
    {
        var server = TestData.LinuxServer();
        var first = TestData.Snapshot(server.Id, cpu: 10);
        var second = TestData.Snapshot(server.Id, cpu: 90);
        var collector = new FakeMetricsCollector();
        var store = new ServerMetricsStore(collector);

        collector.Result = TestData.Success(first);
        await store.RefreshAsync(server);
        collector.Result = TestData.Success(second);
        await store.RefreshAsync(server);

        Assert.Same(second, store.GetLastSnapshot(server.Id));
    }

    [Fact]
    public async Task RefreshAsync_DifferentServers_AreIsolated()
    {
        var serverA = TestData.LinuxServer();
        var serverB = TestData.LinuxServer();
        var snapshotA = TestData.Snapshot(serverA.Id, cpu: 1);
        var collector = new FakeMetricsCollector
        {
            ResultFactory = server => TestData.Success(TestData.Snapshot(server.Id, cpu: 1))
        };
        var store = new ServerMetricsStore(collector);

        await store.RefreshAsync(serverA);

        Assert.NotNull(store.GetLastSnapshot(serverA.Id));
        Assert.Equal(serverA.Id, store.GetLastSnapshot(serverA.Id)!.ServerId);
        Assert.Null(store.GetLastSnapshot(serverB.Id));
    }

    [Fact]
    public async Task RefreshAsync_SameServerConcurrently_IsSingleFlight()
    {
        // Sequential calls while a collection is in flight must share the one
        // Task and trigger the collector exactly once. (The store's
        // single-flight is only exercised for the same ServerId; in the app a
        // server appears as a single card whose refresh button is disabled
        // while running, so genuinely concurrent same-id calls do not occur.)
        var server = TestData.LinuxServer();
        var collector = new FakeMetricsCollector();
        var gate = collector.Gate();
        var store = new ServerMetricsStore(collector);

        var first = store.RefreshAsync(server);
        var second = store.RefreshAsync(server);

        Assert.Same(first, second);
        Assert.Equal(1, collector.CallCount);

        gate.SetResult(TestData.Success(TestData.Snapshot(server.Id, cpu: 5)));
        await Task.WhenAll(first, second);
        Assert.True((await first).IsSuccess);
    }

    [Fact]
    public async Task RefreshAsync_AfterCompletion_StartsAFreshCollection()
    {
        var server = TestData.LinuxServer();
        var collector = new FakeMetricsCollector
        {
            Result = TestData.Success(TestData.Snapshot(server.Id, cpu: 5))
        };
        var store = new ServerMetricsStore(collector);

        await store.RefreshAsync(server);
        await store.RefreshAsync(server);

        Assert.Equal(2, collector.CallCount);
    }

    [Fact]
    public async Task RefreshAsync_Failure_DoesNotStoreSnapshotAndPreservesError()
    {
        var server = TestData.LinuxServer();
        var collector = new FakeMetricsCollector
        {
            Result = TestData.Failure(MetricsCollectionErrorCode.ConnectionFailed, TestData.Connected() with
            {
                State = ServerConnectionState.AuthenticationFailed
            })
        };
        var store = new ServerMetricsStore(collector);

        var result = await store.RefreshAsync(server);

        Assert.False(result.IsSuccess);
        Assert.Equal(MetricsCollectionErrorCode.ConnectionFailed, result.ErrorCode);
        Assert.Null(store.GetLastSnapshot(server.Id));
    }

    [Fact]
    public async Task RefreshAsync_CancelledToken_PropagatesTokenAndReturnsCancelledWithoutSnapshot()
    {
        var server = TestData.LinuxServer();
        var collector = new FakeMetricsCollector();
        var store = new ServerMetricsStore(collector);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await store.RefreshAsync(server, cts.Token);

        Assert.Equal(cts.Token, collector.LastCancellationToken);
        Assert.Equal(MetricsCollectionErrorCode.Cancelled, result.ErrorCode);
        Assert.Null(store.GetLastSnapshot(server.Id));
    }

    [Fact]
    public async Task RefreshAsync_FailureAfterEarlierSuccess_KeepsPreviousSnapshot()
    {
        var server = TestData.LinuxServer();
        var good = TestData.Snapshot(server.Id, cpu: 33);
        var collector = new FakeMetricsCollector();
        var store = new ServerMetricsStore(collector);

        collector.Result = TestData.Success(good);
        await store.RefreshAsync(server);

        collector.Result = TestData.Failure(MetricsCollectionErrorCode.TimedOut);
        var failed = await store.RefreshAsync(server);

        Assert.False(failed.IsSuccess);
        Assert.Same(good, store.GetLastSnapshot(server.Id));
    }

    [Fact]
    public async Task Remove_ClearsStoredSnapshot()
    {
        var server = TestData.LinuxServer();
        var collector = new FakeMetricsCollector
        {
            Result = TestData.Success(TestData.Snapshot(server.Id, cpu: 5))
        };
        var store = new ServerMetricsStore(collector);
        await store.RefreshAsync(server);

        store.Remove(server.Id);

        Assert.Null(store.GetLastSnapshot(server.Id));
    }

    [Fact]
    public void RefreshAsync_NullServer_Throws()
    {
        var store = new ServerMetricsStore(new FakeMetricsCollector());

        // ArgumentNullException.ThrowIfNull throws synchronously, before any Task.
        Assert.Throws<ArgumentNullException>(() => { _ = store.RefreshAsync(null!); });
    }
}
