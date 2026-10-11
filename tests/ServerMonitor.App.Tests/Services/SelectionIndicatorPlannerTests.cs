using ServerMonitor.App.Services.Motion;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// UI.11 H01/H03 (Prism §2.3/§2.5, Cortex §7 testability). The pure decision behind the ONE sliding indicator per track:
/// first layout snaps, a selection animates to the newest target (retarget, never queue), a remount on the same target
/// keeps the running slide, resize / DPI / language snap, Reduced Motion snaps.
/// </summary>
public sealed class SelectionIndicatorPlannerTests
{
    // The History range track: five equal star columns (74 + 4 spacing).
    private static readonly IndicatorRect[] Range =
    [
        new(0, 0, 82.4, 36), new(86.4, 0, 82.4, 36), new(172.8, 0, 82.4, 36), new(259.2, 0, 82.4, 36), new(345.6, 0, 82.4, 36)
    ];

    // The Workloads filter: auto-width items of DIFFERENT widths ("Todos · 12" 124, "Com problemas · 2" 176).
    private static readonly IndicatorRect[] Filter = [new(0, 0, 124, 36), new(128, 0, 176, 36)];

    private static SelectionIndicatorPlanner Presented(IReadOnlyList<IndicatorRect> items, int index)
    {
        var planner = new SelectionIndicatorPlanner();
        planner.Request(IndicatorCause.FirstLayout);
        Assert.Equal(IndicatorAction.Snap, planner.Flush(items, index, reducedMotion: false).Action);
        return planner;
    }

    [Fact]
    public void FirstLayout_Snaps_NeverSlidesInFromNowhere()
    {
        var planner = new SelectionIndicatorPlanner();
        planner.Request(IndicatorCause.Selection); // even a selection before the first presentation

        var plan = planner.Flush(Range, 2, reducedMotion: false);

        Assert.Equal(IndicatorAction.Snap, plan.Action);
        Assert.Equal(Range[2], plan.Target);
        Assert.Equal(Range[2], planner.PresentedTarget);
    }

    [Fact]
    public void Selection_AnimatesToTheCheckedItemsRect()
    {
        var planner = Presented(Range, 0);
        planner.Request(IndicatorCause.Selection);

        var plan = planner.Flush(Range, 3, reducedMotion: false);

        Assert.Equal(new IndicatorPlan(IndicatorAction.Animate, Range[3]), plan);
    }

    [Fact]
    public void Target_IncludesTheAutoWidthOfTheDestination()
    {
        var planner = Presented(Filter, 0);
        planner.Request(IndicatorCause.Selection);

        var plan = planner.Flush(Filter, 1, reducedMotion: false);

        Assert.Equal(IndicatorAction.Animate, plan.Action);
        Assert.Equal(176, plan.Target.Width);
        Assert.Equal(128, plan.Target.X);
    }

    [Fact]
    public void SelectionAndItsOwnRemeasure_InOnePass_SlideToTheFinalWidth()
    {
        // Selecting an auto-width item may re-measure it (its SizeChanged = Resize) before the flush: the selection wins,
        // so the indicator still SLIDES - and to the re-measured width, not a stale one.
        var planner = Presented(Filter, 0);
        planner.Request(IndicatorCause.Selection);
        planner.Request(IndicatorCause.Resize);
        IndicatorRect[] remeasured = [new(0, 0, 124, 36), new(128, 0, 181, 36)];

        var plan = planner.Flush(remeasured, 1, reducedMotion: false);

        Assert.Equal(new IndicatorPlan(IndicatorAction.Animate, remeasured[1]), plan);
    }

    [Fact]
    public void RapidRetarget_0_1_2_0_ConvergesOnTheLast_WithOneAnimatePerInput()
    {
        var planner = Presented(Range, 0);
        var plans = new List<IndicatorPlan>();
        foreach (var index in new[] { 1, 2, 0 })
        {
            planner.Request(IndicatorCause.Selection);
            plans.Add(planner.Flush(Range, index, reducedMotion: false));
        }

        Assert.Equal([IndicatorAction.Animate, IndicatorAction.Animate, IndicatorAction.Animate], plans.Select(p => p.Action));
        Assert.Equal([Range[1], Range[2], Range[0]], plans.Select(p => p.Target));
        Assert.Equal(Range[0], planner.PresentedTarget);
        Assert.False(planner.HasPending); // nothing queued behind the last input
        Assert.Equal(IndicatorAction.NoOp, planner.Flush(Range, 0, reducedMotion: false).Action);
    }

    [Fact]
    public void BurstWithinOneFlush_PlansOnlyTheNewestTarget()
    {
        // Arrow keys at key-repeat speed: several Checked before one Low-priority flush - one animation, to the last.
        var planner = Presented(Range, 0);
        planner.Request(IndicatorCause.Selection);
        planner.Request(IndicatorCause.Selection);
        planner.Request(IndicatorCause.Selection);

        var plan = planner.Flush(Range, 3, reducedMotion: false);

        Assert.Equal(new IndicatorPlan(IndicatorAction.Animate, Range[3]), plan);
        Assert.Equal(IndicatorAction.NoOp, planner.Flush(Range, 3, reducedMotion: false).Action);
    }

    [Fact]
    public void ReselectingThePresentedItem_IsANoOp()
    {
        var planner = Presented(Range, 1);
        planner.Request(IndicatorCause.Selection);

        Assert.Equal(IndicatorAction.NoOp, planner.Flush(Range, 1, reducedMotion: false).Action);
    }

    [Fact]
    public void ReducedMotion_SelectionSnaps()
    {
        var planner = Presented(Range, 0);
        planner.Request(IndicatorCause.Selection);

        Assert.Equal(new IndicatorPlan(IndicatorAction.Snap, Range[4]), planner.Flush(Range, 4, reducedMotion: true));
    }

    [Fact]
    public void ReducedMotionTurningOn_SnapsTheRunningSlideEvenOnTheSameTarget()
    {
        var planner = Presented(Range, 0);
        planner.Request(IndicatorCause.Selection);
        Assert.Equal(IndicatorAction.Animate, planner.Flush(Range, 2, reducedMotion: false).Action);

        planner.Request(IndicatorCause.ReducedMotionChanged);

        Assert.Equal(new IndicatorPlan(IndicatorAction.Snap, Range[2]), planner.Flush(Range, 2, reducedMotion: true));
    }

    [Theory]
    [InlineData(IndicatorCause.Resize)]
    [InlineData(IndicatorCause.DpiChange)]
    [InlineData(IndicatorCause.Remount)]
    public void LayoutCauses_SnapToTheMovedTarget_NeverAnimate(IndicatorCause cause)
    {
        var planner = Presented(Range, 2);
        IndicatorRect[] resized = [.. Range.Select(r => r with { X = r.X * 1.2, Width = r.Width * 1.2 })];
        planner.Request(cause);

        Assert.Equal(new IndicatorPlan(IndicatorAction.Snap, resized[2]), planner.Flush(resized, 2, reducedMotion: false));
    }

    [Theory]
    [InlineData(IndicatorCause.Resize)]
    [InlineData(IndicatorCause.DpiChange)]
    public void LayoutCauses_MidSlide_SnapToTheNewBounds(IndicatorCause cause)
    {
        var planner = Presented(Range, 0);
        planner.Request(IndicatorCause.Selection);
        Assert.Equal(IndicatorAction.Animate, planner.Flush(Range, 3, reducedMotion: false).Action);
        IndicatorRect[] resized = [.. Range.Select(r => r with { X = r.X + 10 })];

        planner.Request(cause);

        Assert.Equal(new IndicatorPlan(IndicatorAction.Snap, resized[3]), planner.Flush(resized, 3, reducedMotion: false));
    }

    [Fact]
    public void RemountOnTheSameTarget_IsANoOp_SoARunningSlideContinues()
    {
        // F04: the theme selector's click starts the slide; ~1 frame later SaThemeRefresh remounts the page.
        var planner = Presented(Range, 0);
        planner.Request(IndicatorCause.Selection);
        Assert.Equal(IndicatorAction.Animate, planner.Flush(Range, 2, reducedMotion: false).Action);

        planner.Request(IndicatorCause.Remount);

        Assert.Equal(IndicatorAction.NoOp, planner.Flush(Range, 2, reducedMotion: false).Action);
        Assert.Equal(Range[2], planner.PresentedTarget);
    }

    [Fact]
    public void RapidThemeClicksWithRemounts_ConvergeOnTheLastSelection()
    {
        // Sistema -> Claro -> Escuro -> Sistema, each followed by a remount (a theme change), at ~30 ms.
        IndicatorRect[] theme = [new(0, 4, 88, 28), new(92, 4, 88, 28), new(184, 4, 88, 28)];
        var planner = Presented(theme, 0);
        var actions = new List<IndicatorAction>();
        foreach (var index in new[] { 1, 2, 0 })
        {
            planner.Request(IndicatorCause.Selection);
            actions.Add(planner.Flush(theme, index, reducedMotion: false).Action);
            planner.Request(IndicatorCause.Remount);
            actions.Add(planner.Flush(theme, index, reducedMotion: false).Action);
        }

        Assert.Equal(
            [IndicatorAction.Animate, IndicatorAction.NoOp, IndicatorAction.Animate, IndicatorAction.NoOp, IndicatorAction.Animate, IndicatorAction.NoOp],
            actions);
        Assert.Equal(theme[0], planner.PresentedTarget);
    }

    [Fact]
    public void SelectionAndRemountInOnePass_StillAnimates()
    {
        var planner = Presented(Range, 0);
        planner.Request(IndicatorCause.Remount);
        planner.Request(IndicatorCause.Selection);

        Assert.Equal(IndicatorAction.Animate, planner.Flush(Range, 1, reducedMotion: false).Action);
    }

    [Fact]
    public void NothingChecked_Hides_AndTheNextSelectionSnapsInsteadOfSliding()
    {
        // The sidebar on a page that belongs to no destination: no item is checked.
        var planner = Presented(Range, 1);
        planner.Request(IndicatorCause.Selection);
        Assert.Equal(IndicatorAction.Hide, planner.Flush(Range, -1, reducedMotion: false).Action);
        Assert.Null(planner.PresentedTarget);

        planner.Request(IndicatorCause.Selection);

        Assert.Equal(new IndicatorPlan(IndicatorAction.Snap, Range[3]), planner.Flush(Range, 3, reducedMotion: false));
    }

    [Fact]
    public void ACollapsedCheckedItem_HasNoTarget()
    {
        IndicatorRect[] items = [new(0, 0, 80, 36), default];

        Assert.Null(SelectionIndicatorPlanner.TargetFor(items, 1));
        Assert.Null(SelectionIndicatorPlanner.TargetFor(items, 2));
        Assert.Null(SelectionIndicatorPlanner.TargetFor(items, -1));
        Assert.Equal(items[0], SelectionIndicatorPlanner.TargetFor(items, 0));
    }

    [Fact]
    public void WithoutAPendingRequest_FlushIsANoOp()
    {
        var planner = Presented(Range, 0);

        Assert.Equal(IndicatorAction.NoOp, planner.Flush(Range, 4, reducedMotion: false).Action);
        Assert.Equal(Range[0], planner.PresentedTarget);
    }

    [Fact]
    public void LayoutRoundingNoise_IsNotANewTarget()
    {
        var planner = Presented(Range, 1);
        IndicatorRect[] noisy = [.. Range.Select(r => r with { X = r.X + 0.004 })];
        planner.Request(IndicatorCause.Resize);

        Assert.Equal(IndicatorAction.NoOp, planner.Flush(noisy, 1, reducedMotion: false).Action);
    }

    [Theory]
    [InlineData(true, true, false, true)]   // rect host, Light
    [InlineData(true, false, false, false)] // Dark: already visible (#242424 on #161616)
    [InlineData(true, true, true, false)]   // High Contrast keeps the system Highlight mapping
    [InlineData(false, true, false, false)] // pill / theme / nav hosts never ask for it
    public void F20_TheLightHairline_IsLightOnly_RectOnly_NeverHighContrast(bool host, bool light, bool highContrast, bool draws)
    {
        Assert.Equal(draws, SelectionIndicatorRules.DrawsLightOutline(host, light, highContrast));
    }
}
