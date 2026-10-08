using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Windowing;

/// <summary>
/// UI.8 fix round c4, Beacon B-7 (Major, from Cortex C2-1). MEASURED (DIAG at 6a2eabd): leaving a MAXIMIZED Standard, the
/// coordinator captured the maximized rect as the Standard bounds; ApplyBounds(Compact) moved the still-maximized window
/// (WinAppSDK keeps it Maximized and keeps its restored rect); the window's Changed handler then ran HoldCompactRestored,
/// whose Restore() returned it to the STANDARD restored rect, which the end-of-transition capture recorded as the Compact
/// bounds. Now: the presenter is restored FIRST, inside the transition; a maximized window is never captured; "Standard was
/// maximized" is remembered for the session only (no persisted field) and Standard is maximized again on the way back; the
/// B-2 hold puts the Compact window back on ITS saved bounds. The fake reproduces the measured presenter semantics and records
/// the call order.
/// </summary>
public sealed class Ui8CompactMaximizedStandardTests
{
    private static readonly WindowBounds StandardRect = new(100, 80, 780, 760);

    [Fact]
    public void AMaximizedStandard_EntersCompact_RestoredFirst_OnTheSavedCompactBounds_AndRecordsNoMaximizedRect()
    {
        var (adapter, store, coordinator, compact) = StandardAtItsRectWithCompactBoundsSaved();
        adapter.Maximize(); // the user's caption button
        adapter.Calls.Clear();

        coordinator.SwitchTo(WindowMode.Compact);

        var restore = adapter.Calls.IndexOf("Restore");
        var apply = adapter.Calls.IndexOf($"ApplyBounds {compact.X},{compact.Y} {compact.Width}x{compact.Height}");
        Assert.True(restore >= 0 && apply > restore, string.Join(" | ", adapter.Calls));
        Assert.False(adapter.IsMaximized);
        Assert.Equal(compact, adapter.CurrentBounds);
        Assert.InRange(compact.Width, WindowSizeConstraints.Compact.MinWidth, WindowSizeConstraints.Compact.MaxWidth);
        Assert.InRange(compact.Height, WindowSizeConstraints.Compact.MinHeight, WindowSizeConstraints.Compact.MaxHeight);

        coordinator.PersistCurrentBounds();
        Assert.Equal(StandardRect, store.Saved.StandardBounds);
        Assert.Equal(compact, store.Saved.CompactBounds);
    }

    [Fact]
    public void BackToStandard_ItIsMaximizedAgain_OverItsUntouchedRestoredRect()
    {
        var (adapter, store, coordinator, _) = StandardAtItsRectWithCompactBoundsSaved();
        adapter.Maximize();
        coordinator.SwitchTo(WindowMode.Compact);
        adapter.Calls.Clear();

        coordinator.SwitchTo(WindowMode.Standard);

        Assert.True(adapter.IsMaximized);
        Assert.Equal("Maximize", adapter.Calls[^1]);
        Assert.Equal(StandardRect, adapter.RestoredBounds);
        coordinator.PersistCurrentBounds();
        Assert.Equal(StandardRect, store.Saved.StandardBounds);

        adapter.Restore(); // the user un-maximizes: back to the rect it had before
        Assert.Equal(StandardRect, adapter.CurrentBounds);
    }

    [Fact]
    public void KCycles_FromAMaximizedStandard_AreStable()
    {
        var (adapter, store, coordinator, compact) = StandardAtItsRectWithCompactBoundsSaved();
        adapter.Maximize();

        for (var cycle = 1; cycle <= 5; cycle++)
        {
            coordinator.SwitchTo(WindowMode.Compact);
            Assert.Equal(compact, adapter.CurrentBounds);
            Assert.False(adapter.IsMaximized);

            coordinator.SwitchTo(WindowMode.Standard);
            Assert.True(adapter.IsMaximized);
            Assert.Equal(StandardRect, adapter.RestoredBounds);
        }

        coordinator.PersistCurrentBounds();
        Assert.Equal(StandardRect, store.Saved.StandardBounds);
        Assert.Equal(compact, store.Saved.CompactBounds);
    }

    [Fact]
    public void ANonMaximizedStandard_IsUnchanged_NoRestoreNoMaximize()
    {
        var (adapter, store, coordinator, compact) = StandardAtItsRectWithCompactBoundsSaved();
        adapter.Calls.Clear();

        coordinator.SwitchTo(WindowMode.Compact);
        Assert.Equal(compact, adapter.CurrentBounds);
        coordinator.SwitchTo(WindowMode.Standard);

        Assert.DoesNotContain("Restore", adapter.Calls);
        Assert.DoesNotContain("Maximize", adapter.Calls);
        Assert.False(adapter.IsMaximized);
        Assert.Equal(StandardRect, adapter.CurrentBounds);
        coordinator.PersistCurrentBounds();
        Assert.Equal(StandardRect, store.Saved.StandardBounds);
    }

    [Fact]
    public void AStandardRestoredBeforeLeaving_IsNotMaximizedOnReturn()
    {
        var (adapter, _, coordinator, _) = StandardAtItsRectWithCompactBoundsSaved();
        adapter.Maximize();
        coordinator.SwitchTo(WindowMode.Compact);
        coordinator.SwitchTo(WindowMode.Standard); // maximized again
        adapter.Restore();                          // the user un-maximizes in Standard

        coordinator.SwitchTo(WindowMode.Compact);
        coordinator.SwitchTo(WindowMode.Standard);

        Assert.False(adapter.IsMaximized);
        Assert.Equal(StandardRect, adapter.CurrentBounds);
    }

    /// <summary>The B-2 hold never pulls the Compact window to the presenter's restored rect when that is the Standard one.</summary>
    [Fact]
    public void TheMaximizeHold_PutsCompactBackOnItsOwnBounds_NotOnTheStandardRect()
    {
        var (adapter, _, coordinator, compact) = StandardAtItsRectWithCompactBoundsSaved();
        coordinator.SwitchTo(WindowMode.Compact);
        adapter.RestoredBounds = StandardRect; // the presenter remembers the Standard rect as its restored one
        adapter.IsMaximized = true;
        adapter.CurrentBounds = adapter.MaximizedBounds;

        Assert.True(coordinator.HoldCompactRestored());

        Assert.False(adapter.IsMaximized);
        Assert.Equal(compact, adapter.CurrentBounds);
    }

    // Standard placed at (100,80) 780×760, one plain Compact visit (its saved bounds), back to Standard, not maximized.
    private static (RecordingPlacementAdapter Adapter, FakeWindowPlacementStore Store, WindowModeCoordinator Coordinator, WindowBounds Compact)
        StandardAtItsRectWithCompactBoundsSaved()
    {
        var adapter = new RecordingPlacementAdapter();
        var store = new FakeWindowPlacementStore();
        var coordinator = new WindowModeCoordinator(adapter, store, NullLogger<WindowModeCoordinator>.Instance);
        coordinator.Initialize();
        adapter.ApplyBounds(StandardRect); // the user moved/resized the Standard window
        coordinator.CaptureCurrentBounds();

        coordinator.SwitchTo(WindowMode.Compact);
        var compact = adapter.CurrentBounds;
        coordinator.SwitchTo(WindowMode.Standard);
        Assert.Equal(StandardRect, adapter.CurrentBounds);
        Assert.False(adapter.IsMaximized);
        return (adapter, store, coordinator, compact);
    }
}
