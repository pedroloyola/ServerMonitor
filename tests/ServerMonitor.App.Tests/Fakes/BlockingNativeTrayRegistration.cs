using ServerMonitor.App.Shell.Tray;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// The native tray boundary, with the ability to PARK INSIDE a call under the test's control.
/// <para>
/// A test that cannot stop the world in the middle of a native call proves none of the S2-T guarantees:
/// not that the deadline terminalizes while a call is outstanding, not that a late success is discarded,
/// and not that compensation follows. So every operation can be made to block until the test releases
/// it, and every call is counted so a mutation that emits an Add after Release fails on the COUNT rather
/// than on the observed state.
/// </para>
/// </summary>
internal sealed class BlockingNativeTrayRegistration : INativeTrayRegistration
{
    private readonly object _sync = new();

    /// <summary>Signalled once an Add has been entered. Lets the test park the world precisely.</summary>
    internal ManualResetEventSlim AddEntered { get; } = new(false);

    /// <summary>Signalled once a Delete has been entered.</summary>
    internal ManualResetEventSlim DeleteEntered { get; } = new(false);

    /// <summary>Blocks Add while unset. Starts SET, so Add returns immediately unless a test parks it.</summary>
    internal ManualResetEventSlim AddMayReturn { get; } = new(true);

    /// <summary>Blocks Delete while unset.</summary>
    internal ManualResetEventSlim DeleteMayReturn { get; } = new(true);

    internal int AddCalls { get; private set; }

    internal int SetVersionCalls { get; private set; }

    internal int DeleteCalls { get; private set; }

    internal bool AddResult { get; set; } = true;

    internal bool SetVersionResult { get; set; } = true;

    internal bool DeleteResult { get; set; } = true;

    /// <summary>Operations in the order the shell saw them, so effect ordering can be asserted.</summary>
    internal List<string> Calls { get; } = [];

    /// <summary>
    /// Whether the shell currently holds the icon. Modelled because <c>Shell_NotifyIcon</c> models it:
    /// <c>NIM_DELETE</c> returns FALSE when there is nothing to delete, and a fake that always returned
    /// true for a delete made the redundant-delete rule unfalsifiable — the mutation that removed it
    /// stayed green because no test could ever produce a second delete that failed.
    /// </summary>
    internal bool IconRegistered { get; private set; }

    public bool Add()
    {
        lock (_sync)
        {
            AddCalls++;
            Calls.Add("Add");
        }

        AddEntered.Set();
        AddMayReturn.Wait();

        if (AddResult)
        {
            lock (_sync)
            {
                IconRegistered = true;
            }
        }

        return AddResult;
    }

    public bool SetVersion()
    {
        lock (_sync)
        {
            SetVersionCalls++;
            Calls.Add("SetVersion");
        }

        return SetVersionResult;
    }

    public bool Delete()
    {
        bool wasRegistered;
        lock (_sync)
        {
            DeleteCalls++;
            Calls.Add("Delete");
            wasRegistered = IconRegistered;
            IconRegistered = false;
        }

        DeleteEntered.Set();
        DeleteMayReturn.Wait();

        // Deleting an icon the shell does not hold reports false. That is not a malfunction; it is the
        // shell saying there was nothing there.
        return wasRegistered && DeleteResult;
    }

    internal int AddCallsSnapshot
    {
        get { lock (_sync) { return AddCalls; } }
    }

    internal int DeleteCallsSnapshot
    {
        get { lock (_sync) { return DeleteCalls; } }
    }
}
