using ServerMonitor.Core.Backup;

namespace ServerMonitor.Core.Tests.Backup;

// M14.6 §5.5 / Vigil V5-C10 / Cortex-1 / Atlas-1 / Atlas-2: the data-layer restore gate. Deterministic: no
// wall-clock stimulus. The drain timeout runs on a manual clock; writers are released through barriers; the only
// real-time value is an outer deadlock deadline (Guard), never the stimulus or the assertion.
public sealed class ConfigurationWriteGateTests
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Open_LeasesAreGranted()
    {
        var gate = new ConfigurationWriteGate();

        using (gate.EnterWrite())
        using (gate.EnterWrite())
        {
            Assert.False(gate.IsLocked);
        }
    }

    [Fact]
    public async Task Held_RefusesNewLeases_ReleaseResumesThem()
    {
        var gate = new ConfigurationWriteGate();
        var token = await gate.BeginRestoreAsync(DrainTimeout);

        Assert.NotNull(token);
        Assert.True(gate.IsLocked);
        Assert.Throws<ConfigurationLockedException>(gate.EnterWrite);

        gate.Release(token);

        Assert.False(gate.IsLocked);
        gate.EnterWrite().Dispose();
    }

    [Fact]
    public async Task Sealed_IsIrreversible()
    {
        var gate = new ConfigurationWriteGate();
        var token = (await gate.BeginRestoreAsync(DrainTimeout))!;

        gate.Seal(token);

        Assert.True(gate.IsLocked);
        Assert.Throws<InvalidOperationException>(() => gate.Release(token));
        Assert.Throws<ConfigurationLockedException>(gate.EnterWrite);
        Assert.Null(await gate.BeginRestoreAsync(DrainTimeout));
        Assert.True(gate.IsLocked);
    }

    [Fact]
    public async Task BeginRestore_WaitsForAnInFlightLeaseToDrain()
    {
        var clock = new ManualTimeProvider();
        var gate = new ConfigurationWriteGate(clock);
        var lease = await ConcurrentWriterAsync(gate);

        var begin = gate.BeginRestoreAsync(DrainTimeout);
        await Guard(clock.TimerCreated);

        Assert.False(begin.IsCompleted); // the drain is armed and waiting; no clock has moved
        Assert.Throws<ConfigurationLockedException>(gate.EnterWrite);

        lease.Dispose();

        Assert.NotNull(await Guard(begin));
    }

    [Fact]
    public async Task DrainTimeout_ReturnsNull_AndReopensTheGate()
    {
        var clock = new ManualTimeProvider();
        var gate = new ConfigurationWriteGate(clock);
        using var stuck = await ConcurrentWriterAsync(gate);

        var begin = gate.BeginRestoreAsync(DrainTimeout);
        await Guard(clock.TimerCreated);
        clock.Advance(DrainTimeout - TimeSpan.FromTicks(1));
        Assert.False(begin.IsCompleted); // not a tick early

        clock.Advance(TimeSpan.FromTicks(1));

        Assert.Null(await Guard(begin));
        Assert.False(gate.IsLocked);
        gate.EnterWrite().Dispose();
    }

    [Fact]
    public async Task CanceledDrain_Throws_AndReopensTheGate()
    {
        var clock = new ManualTimeProvider();
        var gate = new ConfigurationWriteGate(clock);
        using var stuck = await ConcurrentWriterAsync(gate);
        using var cancellation = new CancellationTokenSource();

        var begin = gate.BeginRestoreAsync(DrainTimeout, cancellation.Token);
        await Guard(clock.TimerCreated);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Guard(begin));
        Assert.False(gate.IsLocked);
    }

    // A profile save is one mutation (credential write + server save): a restore that started draining waits
    // for it instead of breaking it in half.
    [Fact]
    public async Task NestedLeaseInAStillOpenLease_SucceedsWhileARestoreDrains()
    {
        var clock = new ManualTimeProvider();
        var gate = new ConfigurationWriteGate(clock);
        var outer = gate.EnterWrite();
        var begin = gate.BeginRestoreAsync(DrainTimeout);
        await Guard(clock.TimerCreated);

        using (gate.EnterWrite())
        {
            Assert.False(begin.IsCompleted);
        }

        outer.Dispose();
        Assert.NotNull(await Guard(begin));
    }

    [Fact]
    public async Task SecondRestore_IsRefused_WhileTheFirstHoldsTheGate()
    {
        var gate = new ConfigurationWriteGate();

        Assert.NotNull(await gate.BeginRestoreAsync(DrainTimeout));
        Assert.Null(await gate.BeginRestoreAsync(DrainTimeout));
    }

    [Fact]
    public async Task EnsureHeldBy_AcceptsOnlyTheCurrentTokenBeforeCommit()
    {
        var gate = new ConfigurationWriteGate();
        var token = (await gate.BeginRestoreAsync(DrainTimeout))!;

        gate.EnsureHeldBy(token);
        gate.Release(token);
        Assert.Throws<InvalidOperationException>(() => gate.EnsureHeldBy(token));

        var second = (await gate.BeginRestoreAsync(DrainTimeout))!;
        Assert.Throws<InvalidOperationException>(() => gate.EnsureHeldBy(token));
        gate.Seal(second);
        Assert.Throws<InvalidOperationException>(() => gate.EnsureHeldBy(second));
    }

    [Fact]
    public async Task Lease_DoubleDispose_IsHarmless()
    {
        var gate = new ConfigurationWriteGate();
        var lease = gate.EnterWrite();

        lease.Dispose();
        lease.Dispose();

        Assert.NotNull(await gate.BeginRestoreAsync(DrainTimeout));
    }

    // ---------------------------------------------------------------- Cortex-1 / Atlas-1: re-entry is lease identity

    // Atlas probe 1 (gate level): a task started INSIDE a lease inherits the flow; once that lease is disposed it
    // gets no bypass — neither while the restore holds the gate nor after the seal.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskStartedInsideADisposedLease_IsRefused_HeldOrSealed(bool seal)
    {
        var gate = new ConfigurationWriteGate();
        var go = Signal();
        Task<Exception?> child;
        using (gate.EnterWrite())
        {
            child = Task.Run<Exception?>(async () =>
            {
                await go.Task.ConfigureAwait(false);
                return Record.Exception(() => gate.EnterWrite().Dispose());
            });
        }

        var token = (await gate.BeginRestoreAsync(DrainTimeout))!;
        if (seal)
        {
            gate.Seal(token);
        }

        go.SetResult();

        Assert.IsType<ConfigurationLockedException>(await Guard(child));
    }

    // Atlas probe 2: a timer created inside a lease fires after dispose + seal.
    [Fact]
    public async Task TimerCreatedInsideALease_CannotEnterAfterTheSeal()
    {
        var gate = new ConfigurationWriteGate();
        var result = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outer = gate.EnterWrite();
        using var timer = new Timer(
            _ => result.TrySetResult(Record.Exception(() => gate.EnterWrite().Dispose())),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
        outer.Dispose();
        gate.Seal((await gate.BeginRestoreAsync(DrainTimeout))!);

        timer.Change(0, Timeout.Infinite);

        Assert.IsType<ConfigurationLockedException>(await Guard(result.Task));
    }

    // Positive control (Atlas): an exception inside `using` releases the lease, so the drain is immediate.
    [Fact]
    public async Task ExceptionWithinALease_ReleasesIt()
    {
        var gate = new ConfigurationWriteGate();

        Assert.Throws<IOException>((Action)(() =>
        {
            using var lease = gate.EnterWrite();
            throw new IOException("injected");
        }));

        Assert.NotNull(await gate.BeginRestoreAsync(TimeSpan.Zero));
    }

    // Positive control (Atlas): a ConfigureAwait(false) continuation inside a LIVE lease keeps its legitimate
    // nesting during the drain, and the drain completes once the writer disposes.
    [Fact]
    public async Task ConfigureAwaitFalseContinuation_InsideALiveLease_NestsDuringTheDrain()
    {
        var clock = new ManualTimeProvider();
        var gate = new ConfigurationWriteGate(clock);
        var resume = Signal();

        async Task Writer()
        {
            using var outer = gate.EnterWrite();
            await resume.Task.ConfigureAwait(false);
            using var nested = gate.EnterWrite();
        }

        var writer = Writer();
        var drain = gate.BeginRestoreAsync(DrainTimeout);
        await Guard(clock.TimerCreated);
        Assert.False(drain.IsCompleted);

        resume.SetResult();
        await Guard(writer);

        Assert.NotNull(await Guard(drain));
    }

    // A still-open outer lease keeps its nested writes during the drain, including from a child task.
    [Fact]
    public async Task ChildTaskOfAStillOpenLease_MayNestWhileDraining()
    {
        var clock = new ManualTimeProvider();
        var gate = new ConfigurationWriteGate(clock);
        var outer = gate.EnterWrite();
        var begin = gate.BeginRestoreAsync(DrainTimeout);
        await Guard(clock.TimerCreated);

        await Guard(Task.Run(() => gate.EnterWrite().Dispose()));

        Assert.False(begin.IsCompleted);
        outer.Dispose();
        Assert.NotNull(await Guard(begin));
    }

    // ---------------------------------------------------------------- helpers

    // A writer in ANOTHER call flow (so this flow's leases are not nested in it).
    private static Task<IDisposable> ConcurrentWriterAsync(ConfigurationWriteGate gate) => Task.Run(gate.EnterWrite);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Outer deadlock deadline only: never the stimulus, never the assertion.
    private static Task Guard(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30));

    private static Task<T> Guard<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(30));
}
