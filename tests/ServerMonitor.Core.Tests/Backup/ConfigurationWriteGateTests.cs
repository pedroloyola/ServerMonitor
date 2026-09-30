using ServerMonitor.Core.Backup;

namespace ServerMonitor.Core.Tests.Backup;

// M14.6 §5.5 / Vigil V5-C10: the data-layer restore gate.
public sealed class ConfigurationWriteGateTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

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
        var token = await gate.BeginRestoreAsync(Short);

        Assert.NotNull(token);
        Assert.True(gate.IsLocked);
        await Task.Run(() => Assert.Throws<ConfigurationLockedException>(gate.EnterWrite));

        gate.Release(token);

        Assert.False(gate.IsLocked);
        gate.EnterWrite().Dispose();
    }

    [Fact]
    public async Task Sealed_IsIrreversible()
    {
        var gate = new ConfigurationWriteGate();
        var token = (await gate.BeginRestoreAsync(Short))!;

        gate.Seal(token);

        Assert.True(gate.IsLocked);
        Assert.Throws<InvalidOperationException>(() => gate.Release(token));
        await Task.Run(() => Assert.Throws<ConfigurationLockedException>(gate.EnterWrite));
        Assert.Null(await gate.BeginRestoreAsync(Short));
        Assert.True(gate.IsLocked);
    }

    [Fact]
    public async Task BeginRestore_WaitsForAnInFlightLeaseToDrain()
    {
        var gate = new ConfigurationWriteGate();

        // Taken in ANOTHER call flow (a concurrent writer), so this flow's leases are not nested in it.
        var lease = await Task.Run(gate.EnterWrite);

        var begin = gate.BeginRestoreAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        Assert.False(begin.IsCompleted);
        await Task.Run(() => Assert.Throws<ConfigurationLockedException>(gate.EnterWrite));

        lease.Dispose();

        Assert.NotNull(await begin.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DrainTimeout_ReturnsNull_AndReopensTheGate()
    {
        var gate = new ConfigurationWriteGate();
        using var stuck = gate.EnterWrite();

        var token = await gate.BeginRestoreAsync(Short);

        Assert.Null(token);
        Assert.False(gate.IsLocked);
        await Task.Run(() => gate.EnterWrite().Dispose());
    }

    [Fact]
    public async Task CanceledDrain_Throws_AndReopensTheGate()
    {
        var gate = new ConfigurationWriteGate();
        using var stuck = gate.EnterWrite();
        using var cancellation = new CancellationTokenSource(Short);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.BeginRestoreAsync(TimeSpan.FromSeconds(10), cancellation.Token));

        Assert.False(gate.IsLocked);
    }

    // A profile save is one mutation (credential write + server save): a restore that started draining waits
    // for it instead of breaking it in half.
    [Fact]
    public async Task NestedLeaseInTheSameFlow_SucceedsWhileARestoreDrains()
    {
        var gate = new ConfigurationWriteGate();
        var outer = gate.EnterWrite();
        var begin = gate.BeginRestoreAsync(TimeSpan.FromSeconds(10));

        using (gate.EnterWrite())
        {
            Assert.False(begin.IsCompleted);
        }

        outer.Dispose();
        Assert.NotNull(await begin.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SecondRestore_IsRefused_WhileTheFirstHoldsTheGate()
    {
        var gate = new ConfigurationWriteGate();
        var first = await gate.BeginRestoreAsync(Short);

        Assert.NotNull(first);
        Assert.Null(await gate.BeginRestoreAsync(Short));
    }

    [Fact]
    public async Task EnsureHeldBy_AcceptsOnlyTheCurrentTokenBeforeCommit()
    {
        var gate = new ConfigurationWriteGate();
        var token = (await gate.BeginRestoreAsync(Short))!;

        gate.EnsureHeldBy(token);
        gate.Release(token);
        Assert.Throws<InvalidOperationException>(() => gate.EnsureHeldBy(token));

        var second = (await gate.BeginRestoreAsync(Short))!;
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

        Assert.NotNull(await gate.BeginRestoreAsync(Short));
    }
}
