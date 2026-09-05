using ServerMonitor.WidgetProvider.Reading;

namespace ServerMonitor.WidgetProvider.Tests.Fakes;

/// <summary>
/// Records the coordinator's arm/disarm/dispose decisions and captures the refresh delegate it was handed,
/// so a test can prove BOTH halves of the wiring: that the pump is armed and disarmed at the right points
/// in the widget lifecycle, and that firing it actually repaints (i.e. the delegate really is
/// <c>RefreshAll</c>, not something inert).
/// </summary>
internal sealed class FakeWidgetRefreshPump : IWidgetRefreshPump
{
    private readonly Action _refresh;

    public FakeWidgetRefreshPump(Action refresh) => _refresh = refresh;

    public int ArmCount { get; private set; }
    public int DisarmCount { get; private set; }
    public int DisposeCount { get; private set; }

    public bool IsArmed { get; private set; }

    /// <summary>When set, Arm/Disarm throw — proving a broken pump cannot break widget handling.</summary>
    public bool ThrowOnStateChange { get; set; }

    public void Arm()
    {
        ArmCount++;
        if (ThrowOnStateChange)
        {
            throw new InvalidOperationException("pump arm failed");
        }

        IsArmed = true;
    }

    public void Disarm()
    {
        DisarmCount++;
        if (ThrowOnStateChange)
        {
            throw new InvalidOperationException("pump disarm failed");
        }

        IsArmed = false;
    }

    public void Dispose()
    {
        DisposeCount++;
        IsArmed = false;
    }

    /// <summary>Fires the delegate the coordinator handed over, as the real pump would.</summary>
    public void FireRefresh() => _refresh();
}
