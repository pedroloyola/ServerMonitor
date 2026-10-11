using ServerMonitor.App.Services.Motion;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// UI.11.1 H02 pure geometry (Prism UI11.1A §2.3/§3.2, Cortex §2.3/§3.4-§3.5, Atlas §2): the Windows threshold in DIP, the
/// interpolated preview, the midpoint prediction on REAL geometry with hysteresis, disabled segments never a target; the
/// planner's drag-end causes; the window activation hub.
/// </summary>
public sealed class SegmentDragRulesTests
{
    private static DragGeometry Geometry(IndicatorRect[] segments, int confirmed = 0, bool[]? enabled = null) =>
        new(segments, enabled ?? segments.Select(_ => true).ToArray(), confirmed, true);

    private static readonly IndicatorRect[] Filter = [new(0, 0, 124, 32), new(128, 0, 176, 32)];
    private static readonly IndicatorRect[] Range = [new(0, 0, 80, 28), new(84, 0, 80, 28), new(168, 0, 80, 28), new(252, 0, 80, 28), new(336, 0, 80, 28)];

    // ---- threshold ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(4, 4, 1.0, 4, 4)]     // 96 DPI
    [InlineData(6, 6, 1.5, 4, 4)]     // 144 DPI: physical pixels -> DIP
    [InlineData(8, 10, 2.0, 4, 5)]
    [InlineData(0, 0, 1.0, 1, 1)]     // a failed metric never becomes a zero threshold
    public void Threshold_Mouse_IsTheWindowsDragRectangleInDip(int cx, int cy, double scale, double x, double y) =>
        Assert.Equal(new DragThreshold(x, y), SegmentDragRules.Threshold(DragPointerDevice.Mouse, cx, cy, scale));

    [Theory]
    [InlineData(DragPointerDevice.Pen)]
    [InlineData(DragPointerDevice.Touch)]
    public void Threshold_PenAndTouch_AreAtLeastEightDip(DragPointerDevice device)
    {
        Assert.Equal(new DragThreshold(8, 8), SegmentDragRules.Threshold(device, 4, 4, 1.0));
        Assert.Equal(new DragThreshold(10, 8), SegmentDragRules.Threshold(device, 20, 4, 2.0));
    }

    [Fact]
    public void Threshold_InvalidScale_CountsAsOne() =>
        Assert.Equal(new DragThreshold(4, 4), SegmentDragRules.Threshold(DragPointerDevice.Mouse, 4, 4, double.NaN));

    // ---- preview geometry ---------------------------------------------------------------------------------------------

    [Fact]
    public void PresentedRect_OnACentre_IsExactlyThatSegment()
    {
        var geometry = Geometry(Filter);

        Assert.Equal(Filter[0], SegmentDragRules.PresentedRect(geometry, 62));
        Assert.Equal(Filter[1], SegmentDragRules.PresentedRect(geometry, 216));
    }

    [Fact]
    public void PresentedRect_BetweenCentres_InterpolatesPositionAndWidth()
    {
        var rect = SegmentDragRules.PresentedRect(Geometry(Filter), 62 + 154 * 0.25);

        Assert.Equal(124 + 52 * 0.25, rect.Width, 6);
        Assert.Equal(62 + 154 * 0.25 - rect.Width / 2, rect.X, 6);
        Assert.Equal(32, rect.Height);
    }

    [Fact]
    public void PresentedRect_IsClampedToTheEnds_NoRubberBand()
    {
        var geometry = Geometry(Filter);

        Assert.Equal(Filter[0], SegmentDragRules.PresentedRect(geometry, -500));
        Assert.Equal(Filter[1], SegmentDragRules.PresentedRect(geometry, 5000));
    }

    // ---- prediction ---------------------------------------------------------------------------------------------------

    /// <summary>Atlas: centre 145 selects the SECOND (measured boundary 139); equal halves of the 304 track would say first.</summary>
    [Fact]
    public void Predict_UnequalWidths_UsesTheMeasuredMidpoint()
    {
        var geometry = Geometry(Filter);

        Assert.Equal(1, SegmentDragRules.Predict(geometry, 145, current: 1));
        Assert.Equal(1, SegmentDragRules.Predict(geometry, 145, current: 0)); // 6 > 4 DIP past the boundary
        Assert.Equal(0, SegmentDragRules.Predict(geometry, 133, current: 0));
    }

    [Theory]
    [InlineData(139.0, 0)]  // on the boundary
    [InlineData(142.9, 0)]  // inside the hysteresis
    [InlineData(143.0, 1)]  // 4 DIP past
    public void Predict_MovingRight_FlipsOnlyPastTheHysteresis(double centre, int expected) =>
        Assert.Equal(expected, SegmentDragRules.Predict(Geometry(Filter), centre, current: 0));

    [Theory]
    [InlineData(139.0, 1)]
    [InlineData(135.1, 1)]
    [InlineData(135.0, 0)]
    public void Predict_MovingLeft_FlipsOnlyPastTheHysteresis(double centre, int expected) =>
        Assert.Equal(expected, SegmentDragRules.Predict(Geometry(Filter), centre, current: 1));

    [Fact]
    public void Predict_AFastJumpOverSeveralSegments_LandsOnTheSegmentUnderTheCentre() =>
        Assert.Equal(4, SegmentDragRules.Predict(Geometry(Range), 370, current: 0));

    [Fact]
    public void Predict_DisabledSegmentsAreNeverTheTarget()
    {
        var geometry = Geometry(Range, confirmed: 0, enabled: [true, true, false, true, true]);

        // The disabled segment's centre (208) is the boundary between the enabled neighbours 1 (124) and 3 (292): it stays.
        Assert.Equal(1, SegmentDragRules.Predict(geometry, 208, current: 1));
        Assert.NotEqual(2, SegmentDragRules.Predict(geometry, 208, current: 0));
        Assert.Equal(3, SegmentDragRules.Predict(geometry, 260, current: 1));
    }

    [Fact]
    public void Predict_WithNoEnabledSegment_IsTheConfirmation() =>
        Assert.Equal(3, SegmentDragRules.Predict(Geometry(Range, confirmed: 3, enabled: [false, false, false, false, false]), 40, current: 1));

    [Fact]
    public void IsValid_RejectsUnusableGeometry()
    {
        Assert.True(SegmentDragRules.IsValid(Geometry(Filter)));
        Assert.False(SegmentDragRules.IsValid(null));
        Assert.False(SegmentDragRules.IsValid(Geometry([Filter[0]])));
        Assert.False(SegmentDragRules.IsValid(new DragGeometry(Filter, [true], 0, true)));
        Assert.False(SegmentDragRules.IsValid(Geometry(Filter, confirmed: 2)));
        Assert.False(SegmentDragRules.IsValid(Geometry(Filter, confirmed: -1)));
        Assert.False(SegmentDragRules.IsValid(Geometry([Filter[0], new IndicatorRect(128, 0, double.NaN, 32)])));
        Assert.False(SegmentDragRules.IsValid(Geometry([Filter[0], new IndicatorRect(128, 0, -1, 32)])));
    }

    // ---- planner: the end of a preview --------------------------------------------------------------------------------

    private static readonly IndicatorRect[] Items = [new(0, 0, 88, 28), new(92, 0, 88, 28)];

    private static SelectionIndicatorPlanner PresentedOn(int index)
    {
        var planner = new SelectionIndicatorPlanner();
        planner.Request(IndicatorCause.FirstLayout);
        planner.Flush(Items, index, reducedMotion: false);
        return planner;
    }

    /// <summary>Cortex R3: the preview displaced the visual, so a release back on the planned rect must still move it.</summary>
    [Fact]
    public void DragEnd_OnTheAlreadyPlannedRect_Settles_NeverNoOp()
    {
        var planner = PresentedOn(1);
        planner.Request(IndicatorCause.DragEnd);

        Assert.Equal(new IndicatorPlan(IndicatorAction.Settle, Items[1]), planner.Flush(Items, 1, reducedMotion: false));
    }

    [Fact]
    public void DragCancel_OnTheAlreadyPlannedRect_AnimatesBack_NeverNoOp()
    {
        var planner = PresentedOn(1);
        planner.Request(IndicatorCause.DragCancel);

        Assert.Equal(new IndicatorPlan(IndicatorAction.Animate, Items[1]), planner.Flush(Items, 1, reducedMotion: false));
    }

    [Theory]
    [InlineData(IndicatorCause.DragEnd)]
    [InlineData(IndicatorCause.DragCancel)]
    public void DragEndAndCancel_UnderReducedMotion_Snap(IndicatorCause cause)
    {
        var planner = PresentedOn(0);
        planner.Request(cause);

        Assert.Equal(new IndicatorPlan(IndicatorAction.Snap, Items[1]), planner.Flush(Items, 1, reducedMotion: true));
    }

    /// <summary>The commit's Checked (Selection) in the same flush as the release settles - one motion, not a 300 ms slide.</summary>
    [Fact]
    public void DragEnd_WinsOverTheCommitsSelection_InTheSameFlush()
    {
        var planner = PresentedOn(0);
        planner.Request(IndicatorCause.DragEnd);
        planner.Request(IndicatorCause.Selection);
        planner.Request(IndicatorCause.Resize);

        Assert.Equal(new IndicatorPlan(IndicatorAction.Settle, Items[1]), planner.Flush(Items, 1, reducedMotion: false));
        Assert.Equal(Items[1], planner.PresentedTarget);
    }

    [Fact]
    public void DragCancel_WithAnExternalSelection_AnimatesOnceToTheRealSelection()
    {
        var planner = PresentedOn(0);
        planner.Request(IndicatorCause.Selection);
        planner.Request(IndicatorCause.DragCancel);

        Assert.Equal(new IndicatorPlan(IndicatorAction.Animate, Items[1]), planner.Flush(Items, 1, reducedMotion: false));
        Assert.Equal(new IndicatorPlan(IndicatorAction.NoOp, Items[1]), planner.Flush(Items, 1, reducedMotion: false));
    }

    // ---- window activation hub ----------------------------------------------------------------------------------------

    [Fact]
    public void Hub_DeliversOnlyToItsOwnRoot_AndDetaches()
    {
        var hub = new WindowActivationHub();
        var root = new object();
        var other = new object();
        var seen = new List<bool>();
        var subscription = hub.Subscribe(root, seen.Add);

        hub.Publish(other, false);
        hub.Publish(null, false);
        Assert.Empty(seen);

        hub.Publish(root, false);
        hub.Publish(root, true);
        Assert.Equal([false, true], seen);

        subscription.Dispose();
        subscription.Dispose(); // idempotent
        hub.Publish(root, false);
        Assert.Equal(2, seen.Count);
        Assert.Equal(0, hub.Count);
    }

    /// <summary>Production keys are AppWindow ids (a struct): two boxes of the same id are the same window.</summary>
    [Fact]
    public void Hub_ComparesWindowIdsByValue()
    {
        var hub = new WindowActivationHub();
        var seen = new List<bool>();
        hub.Subscribe((object)42UL, seen.Add);

        hub.Publish((object)42UL, false);
        hub.Publish((object)43UL, false);

        Assert.Equal([false], seen);
    }

    [Fact]
    public void Hub_AHandlerThatDetachesWhilePublishing_DoesNotBreakTheOthers()
    {
        var hub = new WindowActivationHub();
        var root = new object();
        var calls = 0;
        IDisposable? first = null;
        first = hub.Subscribe(root, _ => { calls++; first!.Dispose(); });
        hub.Subscribe(root, _ => calls++);

        hub.Publish(root, false);

        Assert.Equal(2, calls);
        Assert.Equal(1, hub.Count);
    }
}
