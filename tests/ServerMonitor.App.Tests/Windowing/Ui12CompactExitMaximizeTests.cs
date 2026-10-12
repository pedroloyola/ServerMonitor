using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Windowing;

/// <summary>
/// UI.12 COMPACT-EXIT-MAXIMIZE (Cortex C3-1). The shared "bring it back" block forced <c>WindowState = Normal</c> on every
/// surfacing, so the Compact exits that go through <c>RestoreAndActivateStandard</c> (row → Detail, Add, Manage hidden,
/// deep-link, Settings) re-maximized Standard in the coordinator and then un-maximized it in the show step; the tray
/// Compact toggle un-maximized Standard before switching (so it never came back maximized), and tray "Abrir" un-maximized a
/// visible maximized window. Now only a minimized window is restored. Every transition is exercised from normal, maximized
/// and minimized, over the REAL coordinator and <see cref="StandardSurfacing"/>, with the presenter fake (maximize/restore
/// semantics measured in UI.8 c4; minimize→restore-to-previous-state is documented Win32, not measured). The restore step
/// is the presenter's Restore(), which is what WinUIEx's <c>WindowState = Normal</c> does (per Cortex C3-1; runtime QA
/// confirms the end-to-end result).
/// </summary>
public sealed partial class Ui12CompactExitMaximizeTests
{
    private static readonly WindowBounds StandardRect = new(100, 80, 780, 760);

    public enum Start
    {
        Normal,
        Maximized,
        MinimizedFromNormal,
        MinimizedFromMaximized,
    }

    [Theory]
    [InlineData(Start.Normal, false)]
    [InlineData(Start.Maximized, true)]
    [InlineData(Start.MinimizedFromNormal, false)]
    [InlineData(Start.MinimizedFromMaximized, true)]
    public void ShowAndActivate_RestoresOnlyAMinimizedWindow_AndKeepsMaximized(Start start, bool maximizedAfter)
    {
        var adapter = new RecordingPlacementAdapter { CurrentBounds = StandardRect, RestoredBounds = StandardRect };
        Put(adapter, start);
        adapter.Calls.Clear();

        ShowAndActivate(adapter);

        Assert.False(adapter.IsMinimized);
        Assert.Equal(maximizedAfter, adapter.IsMaximized);
        var minimized = start is Start.MinimizedFromNormal or Start.MinimizedFromMaximized;
        Assert.Equal(minimized ? ["show", "Restore", "activate"] : ["show", "activate"], adapter.Calls);
    }

    /// <summary>The C3-1 path: Compact → a Standard-only destination, from a Standard that was maximized when it left.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExitingCompactToAStandardDestination_KeepsAMaximizedStandardMaximized(bool compactMinimized)
    {
        var (adapter, coordinator) = StandardAtItsRect();
        adapter.Maximize();
        coordinator.SwitchTo(WindowMode.Compact);
        Assert.False(adapter.IsMaximized);
        if (compactMinimized)
        {
            adapter.Minimize();
        }

        adapter.Calls.Clear();

        var ran = StandardSurfacing.Run(() => true, coordinator, () => adapter.IsMinimized, () => ShowAndActivate(adapter));

        Assert.True(ran);
        Assert.Equal(WindowMode.Standard, coordinator.CurrentMode);
        Assert.True(adapter.IsMaximized, string.Join(" | ", adapter.Calls));
        Assert.False(adapter.IsMinimized);
        Assert.Equal(StandardRect, adapter.RestoredBounds);
        Assert.DoesNotContain("Restore", adapter.Calls.SkipWhile(call => call != "Maximize"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExitingCompactToAStandardDestination_LeavesANormalStandardNormal(bool compactMinimized)
    {
        var (adapter, coordinator) = StandardAtItsRect();
        coordinator.SwitchTo(WindowMode.Compact);
        if (compactMinimized)
        {
            adapter.Minimize();
        }

        StandardSurfacing.Run(() => true, coordinator, () => adapter.IsMinimized, () => ShowAndActivate(adapter));

        Assert.Equal(WindowMode.Standard, coordinator.CurrentMode);
        Assert.False(adapter.IsMaximized);
        Assert.False(adapter.IsMinimized);
        Assert.Equal(StandardRect, adapter.CurrentBounds);
    }

    /// <summary>Tray "Modo compacto" (ShowAndActivate, then Toggle) round trip from a maximized Standard.</summary>
    [Theory]
    [InlineData(Start.Maximized)]
    [InlineData(Start.MinimizedFromMaximized)]
    public void TheTrayToggle_FromAMaximizedStandard_ComesBackMaximized(Start start)
    {
        var (adapter, coordinator) = StandardAtItsRect();
        Put(adapter, start);

        ShowAndActivate(adapter);
        coordinator.Toggle();
        Assert.Equal(WindowMode.Compact, coordinator.CurrentMode);
        Assert.False(adapter.IsMaximized);

        ShowAndActivate(adapter);
        coordinator.Toggle();

        Assert.Equal(WindowMode.Standard, coordinator.CurrentMode);
        Assert.True(adapter.IsMaximized, string.Join(" | ", adapter.Calls));
        Assert.Equal(StandardRect, adapter.RestoredBounds);
    }

    /// <summary>Tray "Abrir" / OpenDashboard / second instance keep the mode and must not un-maximize a visible window.</summary>
    [Fact]
    public void TrayOpen_OnAVisibleMaximizedStandard_KeepsItMaximized()
    {
        var (adapter, coordinator) = StandardAtItsRect();
        adapter.Maximize();
        adapter.Calls.Clear();

        ShowAndActivate(adapter);

        Assert.Equal(WindowMode.Standard, coordinator.CurrentMode);
        Assert.True(adapter.IsMaximized);
        Assert.DoesNotContain("Restore", adapter.Calls);
    }

    // ---- Cortex UI.12A C-W1: every surfacing path × every presenter state ----

    /// <summary>The controller's surfacing methods, composed exactly as they are (the composition is pinned by source below).</summary>
    public enum SurfacingPath
    {
        RestoreAndActivate,
        RestoreAndActivateStandard,
        OpenSettings,
        OpenBackgroundSettings,
        ToggleCompactMode,
    }

    public static TheoryData<SurfacingPath, Start> EveryPathFromStandard()
    {
        var data = new TheoryData<SurfacingPath, Start>();
        foreach (var path in Enum.GetValues<SurfacingPath>())
        {
            foreach (var start in Enum.GetValues<Start>())
            {
                data.Add(path, start);
            }
        }

        return data;
    }

    /// <summary>
    /// From Standard in each presenter state: the window ends visible, never minimized, and maximized exactly when it was
    /// maximized before (minimized ones come back to their pre-minimize state). The tray toggle is followed through the
    /// round trip: "Standard maximized → tray Compact → expand (tray again) → maximized again".
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryPathFromStandard))]
    public void EveryPath_FromStandard_KeepsTheMaximizedState(SurfacingPath path, Start start)
    {
        var (adapter, coordinator) = StandardAtItsRect();
        Put(adapter, start);
        var wasMaximized = start is Start.Maximized or Start.MinimizedFromMaximized;
        var navigations = new List<string>();

        Surface(path, adapter, coordinator, navigations);

        Assert.False(adapter.IsMinimized);
        if (path == SurfacingPath.ToggleCompactMode)
        {
            Assert.Equal(WindowMode.Compact, coordinator.CurrentMode);
            Assert.False(adapter.IsMaximized);
            Surface(path, adapter, coordinator, navigations);
        }

        Assert.Equal(WindowMode.Standard, coordinator.CurrentMode);
        Assert.Equal(wasMaximized, adapter.IsMaximized);
        Assert.Equal(StandardRect, adapter.RestoredBounds);
        Assert.Equal(ExpectedNavigations(path), navigations);
    }

    public static TheoryData<SurfacingPath, bool, bool> EveryStandardPathFromCompact()
    {
        var data = new TheoryData<SurfacingPath, bool, bool>();
        foreach (var path in new[]
        {
            SurfacingPath.RestoreAndActivateStandard, SurfacingPath.OpenSettings,
            SurfacingPath.OpenBackgroundSettings, SurfacingPath.ToggleCompactMode,
        })
        {
            foreach (var standardWasMaximized in new[] { false, true })
            {
                data.Add(path, standardWasMaximized, false);
                data.Add(path, standardWasMaximized, true);
            }
        }

        return data;
    }

    /// <summary>From Compact (normal or minimized), every path that leads to Standard restores Standard's maximized state.</summary>
    [Theory]
    [MemberData(nameof(EveryStandardPathFromCompact))]
    public void EveryPath_FromCompact_BackToStandard_RestoresItsMaximizedState(
        SurfacingPath path, bool standardWasMaximized, bool compactMinimized)
    {
        var (adapter, coordinator) = StandardAtItsRect();
        if (standardWasMaximized)
        {
            adapter.Maximize();
        }

        coordinator.SwitchTo(WindowMode.Compact);
        if (compactMinimized)
        {
            adapter.Minimize();
        }

        var navigations = new List<string>();
        Surface(path, adapter, coordinator, navigations);

        Assert.Equal(WindowMode.Standard, coordinator.CurrentMode);
        Assert.False(adapter.IsMinimized);
        Assert.Equal(standardWasMaximized, adapter.IsMaximized);
        Assert.Equal(StandardRect, adapter.RestoredBounds);
        Assert.Equal(ExpectedNavigations(path), navigations);
    }

    /// <summary>Tray "Abrir" never switches the mode: a Compact window comes back Compact, restored if it was minimized.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreAndActivate_FromCompact_StaysCompact(bool compactMinimized)
    {
        var (adapter, coordinator) = StandardAtItsRect();
        adapter.Maximize();
        coordinator.SwitchTo(WindowMode.Compact);
        var compact = adapter.CurrentBounds;
        if (compactMinimized)
        {
            adapter.Minimize();
        }

        Surface(SurfacingPath.RestoreAndActivate, adapter, coordinator, []);

        Assert.Equal(WindowMode.Compact, coordinator.CurrentMode);
        Assert.False(adapter.IsMinimized);
        Assert.False(adapter.IsMaximized);
        Assert.Equal(compact, adapter.CurrentBounds);
    }

    // Mirrors ApplicationWindowController (TryMaterialize = true: a materialized window).
    private static void Surface(
        SurfacingPath path, RecordingPlacementAdapter adapter, WindowModeCoordinator coordinator, List<string> navigations)
    {
        Action show = () => ShowAndActivate(adapter);
        Func<bool> isMinimized = () => adapter.IsMinimized;
        switch (path)
        {
            case SurfacingPath.RestoreAndActivate:
                show();
                break;
            case SurfacingPath.RestoreAndActivateStandard:
                Assert.True(StandardSurfacing.Run(() => true, coordinator, isMinimized, show));
                break;
            case SurfacingPath.OpenSettings:
                Assert.True(StandardSurfacing.Run(() => true, coordinator, isMinimized, show));
                navigations.Add("Settings");
                break;
            case SurfacingPath.OpenBackgroundSettings:
                Assert.True(StandardSurfacing.Run(
                    () => true,
                    coordinator,
                    isMinimized,
                    show,
                    navigateWhileHidden: () => navigations.Add("Settings›Background")));
                break;
            case SurfacingPath.ToggleCompactMode:
                show();
                coordinator.Toggle();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path));
        }
    }

    private static List<string> ExpectedNavigations(SurfacingPath path) => path switch
    {
        SurfacingPath.OpenSettings => ["Settings"],
        SurfacingPath.OpenBackgroundSettings => ["Settings›Background"],
        _ => [],
    };

    // ---- the wiring (WinUI types: pinned by source) ----

    [Fact]
    public void TheController_ShowsThroughWindowActivation_AndSetsNormalOnlyInItsRestoreStep()
    {
        var controller = AppSourceTree.CodeWithoutComments("Services/ApplicationWindowController.cs");

        Assert.Matches(DelegatesToWindowActivation(), controller);
        Assert.Single(NormalAssignment().Matches(controller));
        Assert.Matches(NormalOnlyInRestore(), controller);

        // The two paths that do not go through StandardSurfacing (whose call sites StandardSurfacingTests pins):
        // "Abrir" only shows; the tray toggle shows, THEN toggles (the order the matrix above models).
        Assert.Matches(RestoreAndActivateOnlyShows(), controller);
        Assert.Matches(ToggleShowsThenToggles(), controller);
    }

    [GeneratedRegex(@"public void RestoreAndActivate\(\) => RunOnUiThread\(\(\) =>\s*\{\s*if \(!TryMaterialize\(\)\)\s*\{\s*return;\s*\}\s*ShowAndActivate\(\);\s*logger\.")]
    private static partial Regex RestoreAndActivateOnlyShows();

    [GeneratedRegex(@"public void ToggleCompactMode\(\) => RunOnUiThread\(\(\) =>\s*\{\s*if \(!TryMaterialize\(\)\)\s*\{\s*return;\s*\}\s*ShowAndActivate\(\);\s*modeCoordinator\.Toggle\(\);")]
    private static partial Regex ToggleShowsThenToggles();

    [GeneratedRegex(@"private void ShowAndActivate\(\) => WindowActivation\.ShowAndActivate\(")]
    private static partial Regex DelegatesToWindowActivation();

    [GeneratedRegex(@"isMinimized:\s*IsMinimized,\s*restore:\s*\(\)\s*=>\s*WindowManager\.Get\(_window!\)\.WindowState = WindowState\.Normal,")]
    private static partial Regex NormalOnlyInRestore();

    [GeneratedRegex(@"WindowState\s*=\s*WindowState\.Normal")]
    private static partial Regex NormalAssignment();

    // The production block over the fake: show, restore = the presenter's Restore() (WinUIEx WindowState = Normal), activate.
    private static void ShowAndActivate(RecordingPlacementAdapter adapter) => WindowActivation.ShowAndActivate(
        show: () => adapter.Calls.Add("show"),
        isMinimized: () => adapter.IsMinimized,
        restore: adapter.Restore,
        activate: () => adapter.Calls.Add("activate"));

    private static void Put(RecordingPlacementAdapter adapter, Start start)
    {
        if (start is Start.Maximized or Start.MinimizedFromMaximized)
        {
            adapter.Maximize();
        }

        if (start is Start.MinimizedFromNormal or Start.MinimizedFromMaximized)
        {
            adapter.Minimize();
        }
    }

    // Standard at (100,80) 780×760 with its Compact bounds saved by one plain visit; back in Standard, not maximized.
    private static (RecordingPlacementAdapter Adapter, WindowModeCoordinator Coordinator) StandardAtItsRect()
    {
        var adapter = new RecordingPlacementAdapter();
        var coordinator = new WindowModeCoordinator(adapter, new FakeWindowPlacementStore(), NullLogger<WindowModeCoordinator>.Instance);
        coordinator.Initialize();
        adapter.ApplyBounds(StandardRect);
        coordinator.CaptureCurrentBounds();
        coordinator.SwitchTo(WindowMode.Compact);
        coordinator.SwitchTo(WindowMode.Standard);
        Assert.Equal(StandardRect, adapter.CurrentBounds);
        Assert.False(adapter.IsMaximized);
        return (adapter, coordinator);
    }
}
