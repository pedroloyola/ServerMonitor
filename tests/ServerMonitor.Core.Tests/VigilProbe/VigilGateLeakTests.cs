using ServerMonitor.Core.Backup;

namespace ServerMonitor.Core.Tests.VigilProbe;

// Adopted from Vigil's final review (Vigil-1, .boss/tmp/m14.6-vigil-probe-tests.patch). The drain completes
// immediately (no lease is open when it starts), so the timeout value is never the stimulus.
public sealed class VigilGateLeakTests
{
    // A task spawned inside a lease inherits the flow. After the lease ends and a restore seals the gate, that
    // task must still be refused.
    [Fact]
    public async Task Vigil_FlowSpawnedInsideALease_CannotWriteAfterSeal()
    {
        var gate = new ConfigurationWriteGate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception?> child;
        using (gate.EnterWrite())
        {
            child = Task.Run(async () =>
            {
                await release.Task;
                try
                {
                    using (gate.EnterWrite())
                    {
                    }

                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });
        }

        var token = await gate.BeginRestoreAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(token);
        gate.Seal(token!);
        release.SetResult();
        var result = await child;
        Assert.IsType<ConfigurationLockedException>(result);
    }

    // Same leak while Held (before commit): the write would race the restore's own writes.
    [Fact]
    public async Task Vigil_FlowSpawnedInsideALease_CannotWriteWhileRestoreHolds()
    {
        var gate = new ConfigurationWriteGate();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception?> child;
        using (gate.EnterWrite())
        {
            child = Task.Run(async () =>
            {
                await release.Task;
                try
                {
                    using (gate.EnterWrite())
                    {
                    }

                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            });
        }

        var token = await gate.BeginRestoreAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(token);
        release.SetResult();
        var result = await child;
        Assert.IsType<ConfigurationLockedException>(result);
    }
}
