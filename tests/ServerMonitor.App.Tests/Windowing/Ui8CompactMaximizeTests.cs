using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Windowing;

/// <summary>
/// UI.8 fix round c2, Beacon c1 B-2: Compact is never maximized. <c>IsMaximizable=false</c> clears WS_MAXIMIZEBOX, but the
/// caption button (extended title bar), a title double-click or Win+Up still put the presenter in Maximized (measured by
/// Beacon: IsZoomed=True). The coordinator therefore puts a maximized Compact back to Restored and never records it; the
/// window asks it on every AppWindow change, before it captures anything. Standard keeps its maximize.
/// </summary>
public sealed class Ui8CompactMaximizeTests
{
    private static readonly WindowBounds Maximized = new(0, 0, 1920, 1040);

    [Fact]
    public void AMaximizedCompact_IsRestored_AndNeverRecorded()
    {
        var (adapter, store, coordinator) = Create();
        coordinator.SwitchTo(WindowMode.Compact);
        var restored = adapter.CurrentBounds;
        coordinator.PersistCurrentBounds();
        Assert.Equal(restored, store.Saved.CompactBounds);

        // The shell maximizes it: the presenter reports Maximized and full-screen bounds.
        adapter.RestoredBounds = restored;
        adapter.IsMaximized = true;
        adapter.CurrentBounds = Maximized;

        coordinator.CaptureCurrentBounds();   // a size/position change arriving with the maximize
        coordinator.PersistCurrentBounds();   // e.g. closed or minimized while maximized
        Assert.Equal(restored, store.Saved.CompactBounds);

        Assert.True(coordinator.HoldCompactRestored());
        Assert.Equal(1, adapter.RestoreCount);
        Assert.False(adapter.IsMaximized);
        Assert.Equal(restored, adapter.CurrentBounds);

        coordinator.PersistCurrentBounds();
        Assert.Equal(restored, store.Saved.CompactBounds);
        Assert.Equal(WindowMode.Compact, coordinator.CurrentMode);
    }

    [Fact]
    public void Standard_KeepsItsMaximize_AndARestoredCompactIsLeftAlone()
    {
        var (adapter, _, coordinator) = Create();
        adapter.IsMaximized = true;
        Assert.False(coordinator.HoldCompactRestored()); // Standard may maximize
        Assert.Equal(0, adapter.RestoreCount);

        adapter.IsMaximized = false;
        coordinator.SwitchTo(WindowMode.Compact);
        Assert.False(coordinator.HoldCompactRestored());
        Assert.Equal(0, adapter.RestoreCount);
    }

    [Fact]
    public void TheWindow_AsksBeforeCapturing_OnEveryChange_AndThePresenterStaysNonMaximizableInCompact()
    {
        var code = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        var changed = code[code.IndexOf("private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)", StringComparison.Ordinal)..];
        changed = changed[..changed.IndexOf("private void EnforceSizeLimits", StringComparison.Ordinal)];

        var hold = changed.IndexOf(
            "if ((args.DidPresenterChange || args.DidSizeChange || args.DidPositionChange) && _modeCoordinator.HoldCompactRestored())",
            StringComparison.Ordinal);
        Assert.True(hold >= 0);
        Assert.True(changed.IndexOf("_modeCoordinator.CaptureCurrentBounds();", StringComparison.Ordinal) > hold);
        Assert.True(changed.IndexOf("if (_isEnforcingSize || _modeCoordinator.IsApplyingBounds)", StringComparison.Ordinal) > hold);

        var adapter = AppSourceTree.CodeWithoutComments("Windowing/AppWindowPlacementAdapter.cs");
        Assert.Contains("presenter.IsMaximizable = isStandard;", adapter, StringComparison.Ordinal);
        Assert.Contains("public bool IsMaximized => _appWindow?.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };", adapter, StringComparison.Ordinal);
        Assert.Contains("presenter.Restore();", adapter, StringComparison.Ordinal);
    }

    private static (RecordingPlacementAdapter Adapter, FakeWindowPlacementStore Store, WindowModeCoordinator Coordinator) Create()
    {
        var adapter = new RecordingPlacementAdapter();
        var store = new FakeWindowPlacementStore();
        var coordinator = new WindowModeCoordinator(adapter, store, NullLogger<WindowModeCoordinator>.Instance);
        coordinator.Initialize();
        return (adapter, store, coordinator);
    }
}

