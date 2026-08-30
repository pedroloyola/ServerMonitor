using ServerMonitor.WidgetProvider.Com;

namespace ServerMonitor.WidgetProvider.Tests;

public sealed class ComServerProcessTests
{
    private static ComServerProcess NewProcess(out Func<int> currentCount, out Func<bool> wasSuspended)
    {
        var count = 0;
        var suspended = false;
        currentCount = () => Volatile.Read(ref count);
        wasSuspended = () => Volatile.Read(ref suspended);
        return new ComServerProcess(
            addRef: () => (uint)Interlocked.Increment(ref count),
            release: () => (uint)Interlocked.Decrement(ref count),
            suspend: () => Volatile.Write(ref suspended, true));
    }

    [Fact]
    public void Starts_unreferenced_and_not_exiting()
    {
        var process = NewProcess(out _, out var suspended);
        Assert.False(process.EverReferenced);
        Assert.False(process.IsExiting);
        Assert.False(suspended());
    }

    [Fact]
    public void AddRef_marks_referenced_and_does_not_exit()
    {
        var process = NewProcess(out var count, out _);
        Assert.Equal(1u, process.AddRef());
        Assert.True(process.EverReferenced);
        Assert.False(process.IsExiting);
        Assert.Equal(1, count());
    }

    [Fact]
    public void Release_to_zero_suspends_and_signals_exit()
    {
        var process = NewProcess(out _, out var suspended);
        process.AddRef();

        Assert.Equal(0u, process.Release());

        Assert.True(suspended());          // CoSuspendClassObjects was called
        Assert.True(process.IsExiting);
        Assert.True(process.WaitForExit(TimeSpan.Zero));
    }

    [Fact]
    public void Release_above_zero_does_not_exit()
    {
        var process = NewProcess(out _, out var suspended);
        process.AddRef();
        process.AddRef();

        Assert.Equal(1u, process.Release()); // one object remains

        Assert.False(suspended());
        Assert.False(process.IsExiting);
        Assert.False(process.WaitForExit(TimeSpan.Zero));
    }

    [Fact]
    public void Lock_then_object_lifetimes_compose()
    {
        // A LockServer(true) and a created object both hold the process alive; it exits only when both go.
        var process = NewProcess(out var count, out _);
        process.AddRef(); // lock
        process.AddRef(); // object
        Assert.Equal(2, count());

        Assert.Equal(1u, process.Release());
        Assert.False(process.IsExiting);
        Assert.Equal(0u, process.Release());
        Assert.True(process.IsExiting);
    }
}
