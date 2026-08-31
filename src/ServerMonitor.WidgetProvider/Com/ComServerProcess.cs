using System.Runtime.InteropServices;

namespace ServerMonitor.WidgetProvider.Com;

/// <summary>
/// Implements the official out-of-process COM server lifetime protocol
/// (CoAddRefServerProcess / CoReleaseServerProcess / CoSuspendClassObjects). Each live COM object bumps
/// the per-process reference on construction and releases it on teardown; when the count reaches zero,
/// COM's helper atomically suspends new activations (CoSuspendClassObjects) and we signal exit. This is
/// the correct lifetime barrier — the registry of widgets is NOT — so the server can never revoke while
/// an activation is in flight, and a Create arriving after suspension causes Windows to relaunch the
/// server (ADR-018 §14/§15). The native calls are injectable so the ref-counting and exit signalling can
/// be unit-tested without COM.
/// <para>See https://learn.microsoft.com/windows/win32/com/out-of-process-server-implementation-helpers .</para>
/// </summary>
public sealed class ComServerProcess
{
    private readonly Func<uint> _addRef;
    private readonly Func<uint> _release;
    private readonly Action _suspend;
    private readonly ManualResetEventSlim _exiting = new(false);
    private int _everReferenced;

    public ComServerProcess(Func<uint>? addRef = null, Func<uint>? release = null, Action? suspend = null)
    {
        _addRef = addRef ?? (() => Native.CoAddRefServerProcess());
        _release = release ?? (() => Native.CoReleaseServerProcess());
        _suspend = suspend ?? (() => Native.CoSuspendClassObjects());
    }

    /// <summary>True once at least one COM object has ever been created (used to bound a never-activated launch).</summary>
    public bool EverReferenced => Volatile.Read(ref _everReferenced) != 0;

    /// <summary>True once the last object was released and new activations have been suspended.</summary>
    public bool IsExiting => _exiting.IsSet;

    /// <summary>Call when a COM object is created. Returns the new per-process reference count.</summary>
    public uint AddRef()
    {
        Volatile.Write(ref _everReferenced, 1);
        return _addRef();
    }

    /// <summary>
    /// Call when a COM object is torn down. When the count reaches zero, suspends new activations and
    /// signals exit. Safe to call more than the matching AddRef count would suggest — it only acts on the
    /// zero transition.
    /// </summary>
    public uint Release()
    {
        var remaining = _release();
        if (remaining == 0)
        {
            _suspend();
            _exiting.Set();
        }

        return remaining;
    }

    /// <summary>Waits until exit is signalled or the timeout elapses. Returns true if exiting.</summary>
    public bool WaitForExit(TimeSpan timeout) => _exiting.Wait(timeout);

    private static class Native
    {
        [DllImport("ole32.dll")]
        internal static extern uint CoAddRefServerProcess();

        [DllImport("ole32.dll")]
        internal static extern uint CoReleaseServerProcess();

        [DllImport("ole32.dll")]
        internal static extern int CoSuspendClassObjects();
    }
}
