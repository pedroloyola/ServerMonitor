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
    /// <summary>
    /// Deadlock guard only. Every wait in this class is released by an event (a store call, a barrier completing, a
    /// worker arming its timer); the writer's own timeouts run on the FakeTimeProvider, which a test advances only once
    /// the worker is parked on the timer it needs. So a working writer never reaches this bound, however slow the
    /// runner. The former 5 s bounds were latency expectations over thread-pool workers (CI-flakes, runs 37057158486
    /// and later repetitions).
    /// </summary>
    private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(30);

    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static ServerHistorySample Sample(Guid id, DateTimeOffset at) => new()
    {
        ServerId = id,
        CapturedAtUtc = at,
        Health = ServerHealth.Healthy,
        CpuPercent = 10
    };

    private static (HistoryWriterService writer, HistorySampleChannel channel, FakeServerHistoryStore store, TimerRecordingTimeProvider time) New()
    {
        var channel = new HistorySampleChannel();
        var store = new FakeServerHistoryStore();
        var time = new TimerRecordingTimeProvider();
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
            await store.WaitForWrittenCountAsync(1).WaitAsync(DeadlockGuard);
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
            await store.WaitForRetentionCallsAsync(1).WaitAsync(DeadlockGuard);
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
        // Deterministic: each wait is released by a signal (the store's retention call, the loop arming its 24 h
        // timer), and the 24 h is a single FakeTimeProvider advance made only AFTER that timer exists. Advancing any
        // earlier is a real race: the loop reads the clock, computes "24 h from now", and only then creates the timer,
        // so an advance in that window arms the timer 24 h after the ALREADY-advanced clock (+48 h) and it never fires
        // (independent review r1, MUST-2). The bound is a deadlock guard only; it used to be 5 s.
        // TimerRecordingTimeProvider matches the exact 24 h due time: the clock is frozen until the advance, so the
        // loop's delay is exactly nextRunUtc - now = 24 h.
        var (writer, _, store, time) = New();
        await writer.StartAsync(CancellationToken.None);
        try
        {
            await store.WaitForRetentionCallsAsync(1).WaitAsync(DeadlockGuard);
            Assert.Equal(Now - TimeSpan.FromDays(30), store.LastRetentionCutoff);

            await time.TimerCreatedAsync(TimeSpan.FromHours(24)).WaitAsync(DeadlockGuard); // parked on its daily timer
            time.Advance(TimeSpan.FromHours(24));
            await store.WaitForRetentionCallsAsync(2).WaitAsync(DeadlockGuard);

            // The second run is the daily one, on the advanced clock — and exactly one run per elapsed interval.
            Assert.Equal(Now + TimeSpan.FromHours(24) - TimeSpan.FromDays(30), store.LastRetentionCutoff);
            Assert.Equal(2, store.RetentionCallCount);
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
            Assert.True(await clearTask.WaitAsync(DeadlockGuard));
            Assert.Equal(1, store.WriteBatchCount);
            Assert.Equal(1, store.ClearCallCount);
            Assert.Empty(store.Written);

            Assert.True(channel.TryWrite(Sample(Guid.NewGuid(), Now + TimeSpan.FromSeconds(30))));
            await store.WaitForWrittenCountAsync(1).WaitAsync(DeadlockGuard);
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
            Assert.False(await writer.ClearAsync().WaitAsync(DeadlockGuard));
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
            // Same read-clock-then-arm-timer window as the retention loop: advancing before the recovery loop has
            // armed its 5 s timer would arm it after the advanced clock and the retry would never come. The clock is
            // frozen until the advance, so the loop's delay is exactly the 5 s initial backoff.
            await time.TimerCreatedAsync(TimeSpan.FromSeconds(5)).WaitAsync(DeadlockGuard);
            time.Advance(TimeSpan.FromSeconds(5));
            await store.WaitForInitializeCallsAsync(2).WaitAsync(DeadlockGuard);
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
        // Every wait here is released by an event (the store's WriteEntered signal, the clear barrier's completion);
        // the writer's own timeouts run on the FakeTimeProvider and StopAsync gets an already-cancelled token, so no
        // step depends on elapsed time. The bound is a deadlock guard only. It used to be 5 s, which a busy CI runner
        // exceeded just scheduling the consumer worker onto the thread pool (TimeoutException, run 37057158486).
        var (writer, channel, store, _) = New();
        var blocker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.WriteBlocker = blocker;
        await writer.StartAsync(CancellationToken.None);
        Assert.True(channel.TryWrite(Sample(Guid.NewGuid(), Now)));
        await store.WriteEntered.Task.WaitAsync(DeadlockGuard);
        var clear = writer.ClearAsync();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await writer.StopAsync(cancelled.Token);

        Assert.False(await clear.WaitAsync(DeadlockGuard));
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
            Assert.True(await reset.WaitAsync(DeadlockGuard));
            Assert.True(store.IsAvailable);
            Assert.Equal(1, store.ResetCallCount);
            Assert.Empty(store.Written);

            Assert.True(channel.TryWrite(Sample(Guid.NewGuid(), Now + TimeSpan.FromSeconds(30))));
            await store.WaitForWrittenCountAsync(1).WaitAsync(DeadlockGuard);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
        }
    }
}
