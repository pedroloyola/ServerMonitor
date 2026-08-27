using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.App.Tests.Services;

public sealed class HistoryWriterServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static ServerHistorySample Sample(Guid id, DateTimeOffset at) => new()
    {
        ServerId = id,
        CapturedAtUtc = at,
        Health = ServerHealth.Healthy,
        CpuPercent = 10
    };

    private static (HistoryWriterService writer, HistorySampleChannel channel, FakeServerHistoryStore store, FakeTimeProvider time) New()
    {
        var channel = new HistorySampleChannel();
        var store = new FakeServerHistoryStore();
        var time = new FakeTimeProvider();
        time.SetUtcNow(Now);
        var options = new HistoryStorageOptions { DatabasePath = "unused.db", RetentionPeriod = TimeSpan.FromDays(30) };
        var writer = new HistoryWriterService(channel, store, options, NullLogger<HistoryWriterService>.Instance, time);
        return (writer, channel, store, time);
    }

    [Fact]
    public async Task Start_InitializesStore()
    {
        var (writer, _, store, _) = New();
        await writer.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(1, store.InitializeCount);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EnqueuedSamples_AreDrainedToStore()
    {
        var (writer, channel, store, _) = New();
        var id = Guid.NewGuid();
        Assert.True(channel.TryWrite(Sample(id, Now)));

        await writer.StartAsync(CancellationToken.None);
        try
        {
            await store.WaitForWrittenCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(id, store.Written[0].ServerId);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Retention_RunsAtStartup_WithCorrectCutoff()
    {
        var (writer, _, store, _) = New();
        await writer.StartAsync(CancellationToken.None);
        try
        {
            await store.WaitForRetentionCallsAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Now - TimeSpan.FromDays(30), store.LastRetentionCutoff);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Retention_RunsAgainAfterDailyInterval()
    {
        var (writer, _, store, time) = New();
        await writer.StartAsync(CancellationToken.None);
        try
        {
            await store.WaitForRetentionCallsAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromHours(24));
            await store.WaitForRetentionCallsAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Shutdown_DrainsPending_AndCompletesBounded()
    {
        var (writer, channel, store, _) = New();
        await writer.StartAsync(CancellationToken.None);
        for (var i = 0; i < 5; i++)
        {
            channel.TryWrite(Sample(Guid.NewGuid(), Now + TimeSpan.FromSeconds(i)));
        }

        // StopAsync must return without hanging and the pending samples must have been drained.
        await writer.StopAsync(CancellationToken.None);
        Assert.Equal(5, store.Written.Count);
    }

    [Fact]
    public async Task Shutdown_WithEmptyQueue_CompletesCleanly()
    {
        var (writer, _, _, _) = New();
        await writer.StartAsync(CancellationToken.None);
        await writer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Clear_IsOrderedAfterAcceptedSamples_AndReportsRealSuccess()
    {
        var (writer, channel, store, _) = New();
        Assert.True(channel.TryWrite(Sample(Guid.NewGuid(), Now)));

        var clearTask = writer.ClearAsync();
        await writer.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await clearTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, store.WriteBatchCount);
            Assert.Equal(1, store.ClearCallCount);
            Assert.Empty(store.Written);

            Assert.True(channel.TryWrite(Sample(Guid.NewGuid(), Now + TimeSpan.FromSeconds(30))));
            await store.WaitForWrittenCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Clear_WhenStoreDeleteFails_ReturnsFalse()
    {
        var (writer, _, store, _) = New();
        store.ClearSucceeds = false;
        await writer.StartAsync(CancellationToken.None);
        try
        {
            Assert.False(await writer.ClearAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, store.ClearCallCount);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TransientInitializationFailure_RecoversAfterBoundedBackoff()
    {
        var (writer, _, store, time) = New();
        store.Available = false;
        store.CanRetryInitialization = true;
        store.BecomeAvailableOnInitializeCount = 2;

        await writer.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(1, store.InitializeCount);
            time.Advance(TimeSpan.FromSeconds(5));
            await store.WaitForInitializeCallsAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(store.IsAvailable);
            Assert.False(store.CanRetryInitialization);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Shutdown_WithClearPending_CompletesBarrierFalse_AndDoesNotDisposeActiveWorkerState()
    {
        var (writer, channel, store, _) = New();
        var blocker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.WriteBlocker = blocker;
        await writer.StartAsync(CancellationToken.None);
        Assert.True(channel.TryWrite(Sample(Guid.NewGuid(), Now)));
        await store.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clear = writer.ClearAsync();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await writer.StopAsync(cancelled.Token);

        Assert.False(await clear.WaitAsync(TimeSpan.FromSeconds(5)));
        blocker.TrySetResult(true);
    }

    [Fact]
    public async Task Reset_IsOrderedWithWrites_AndRestoresAvailability()
    {
        var (writer, channel, store, _) = New();
        store.Available = false;
        Assert.True(channel.TryWrite(Sample(Guid.NewGuid(), Now)));
        var reset = writer.ResetAsync();

        await writer.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await reset.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(store.IsAvailable);
            Assert.Equal(1, store.ResetCallCount);
            Assert.Empty(store.Written);

            Assert.True(channel.TryWrite(Sample(Guid.NewGuid(), Now + TimeSpan.FromSeconds(30))));
            await store.WaitForWrittenCountAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
    }
}
