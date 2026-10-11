using ServerMonitor.App.Services.Motion;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// UI.11.1 H02 (Cortex UI11.1A + S0 B0-B4/D1, Atlas UI11.1A §2-§5 + S0 §2/§4). The production drag controller driven
/// through its handlers against a recording port that can re-enter it synchronously, as WinUI does (a capture release
/// raises CaptureLost; a commit raises Checked and, for the theme, a remount). Proves decisions and their order; the
/// routed-event wiring on the real controls is the UI.11.1D runtime gate's.
/// </summary>
public sealed class SegmentDragControllerTests
{
    // The theme selector at 96 DPI (S0: three 88-DIP items, 4 DIP apart) and the services filter (unequal 124/176).
    private static readonly IndicatorRect[] Theme = [new(0, 4, 88, 28), new(92, 4, 88, 28), new(184, 4, 88, 28)];
    private static readonly IndicatorRect[] Filter = [new(0, 0, 124, 32), new(128, 0, 176, 32)];

    private static DragPointer Mouse(double x, double y = 16, uint id = 1, bool primary = true) => new(id, DragPointerDevice.Mouse, x, y, primary);

    private static (SegmentDragController Controller, FakePort Port) Armed(int confirmed = 2, IndicatorRect[]? segments = null, bool[]? enabled = null)
    {
        var port = new FakePort(segments ?? Theme, confirmed, enabled);
        var controller = new SegmentDragController(port);
        port.Controller = controller;
        var origin = (segments ?? Theme)[confirmed];
        controller.Press(Mouse(origin.X + origin.Width / 2), confirmed);
        return (controller, port);
    }

    // ---- arming -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Press_OnTheConfirmedSegment_Arms_WithoutAnyEffect()
    {
        var (controller, port) = Armed();

        Assert.True(controller.IsActive);
        Assert.False(controller.IsDragging);
        Assert.Equal(["Snapshot", "Threshold(Mouse)"], port.Effects);
    }

    [Theory]
    [InlineData(0)] // another segment: a click
    [InlineData(1)]
    [InlineData(-1)] // not on a segment
    public void Press_ElsewhereThanTheConfirmedSegment_StaysAClick(int pressed)
    {
        var port = new FakePort(Theme, 2);
        var controller = new SegmentDragController(port);
        controller.Press(Mouse(44), pressed);
        controller.Move(Mouse(200));
        controller.Release(Mouse(200));

        Assert.False(controller.IsActive);
        Assert.DoesNotContain(port.Effects, e => e.StartsWith("ReleaseOrigin", StringComparison.Ordinal) || e.StartsWith("Commit", StringComparison.Ordinal));
    }

    [Fact]
    public void Press_SecondaryButton_GroupDisabled_SegmentDisabled_InvalidGeometry_NeverArm()
    {
        var secondary = new SegmentDragController(new FakePort(Theme, 2));
        secondary.Press(Mouse(228, primary: false), 2);
        Assert.False(secondary.IsActive);

        var group = new SegmentDragController(new FakePort(Theme, 2) { GroupEnabled = false });
        group.Press(Mouse(228), 2);
        Assert.False(group.IsActive);

        var item = new SegmentDragController(new FakePort(Theme, 2, [true, true, false]));
        item.Press(Mouse(228), 2);
        Assert.False(item.IsActive);

        foreach (var bad in new[] { new IndicatorRect(0, 0, double.NaN, 28), new IndicatorRect(0, 0, 0, 28), new IndicatorRect(double.PositiveInfinity, 0, 88, 28) })
        {
            var controller = new SegmentDragController(new FakePort([Theme[0], bad, Theme[2]], 2));
            controller.Press(Mouse(228), 2);
            Assert.False(controller.IsActive);
        }

        var single = new SegmentDragController(new FakePort([Theme[0]], 0));
        single.Press(Mouse(44), 0);
        Assert.False(single.IsActive);
    }

    // ---- threshold ----------------------------------------------------------------------------------------------------

    [Fact]
    public void BelowThreshold_NeverTransfers_AndTheReleaseIsTheItemsClick()
    {
        var (controller, port) = Armed();
        controller.Move(Mouse(228 - 3.9));
        controller.Release(Mouse(228 - 3.9));

        Assert.False(controller.IsActive);
        Assert.Equal(["Snapshot", "Threshold(Mouse)"], port.Effects); // no transfer, no preview, no commit
    }

    [Fact]
    public void AtTheThreshold_TheCaptureMovesToThePanel_AndThePreviewStarts()
    {
        var (controller, port) = Armed();
        controller.Move(Mouse(228 - 4));

        Assert.True(controller.IsDragging);
        Assert.Equal(["Snapshot", "Threshold(Mouse)", "ReleaseOrigin(1)", "CapturePanel(1)", "BeginPreview", "Present(180.0,88.0|2)"], port.Effects);
    }

    [Fact]
    public void VerticalFirst_Abandons_AndNeverTransfers()
    {
        var (controller, port) = Armed();
        controller.Move(Mouse(228 - 2, 16 + 4));
        controller.Move(Mouse(100, 16 + 4)); // later horizontal travel does not revive it

        Assert.False(controller.IsActive);
        Assert.DoesNotContain(port.Effects, e => e.StartsWith("ReleaseOrigin", StringComparison.Ordinal));
    }

    // ---- preview ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Move_NeverCommits_AndFollowsOneToOne_KeepingTheGrabOffset()
    {
        var port = new FakePort(Theme, 2);
        var controller = new SegmentDragController(port);
        controller.Press(Mouse(250), 2); // grabbed 22 DIP right of the centre
        controller.Move(Mouse(240));
        controller.Move(Mouse(201));

        Assert.DoesNotContain(port.Effects, e => e.StartsWith("Commit", StringComparison.Ordinal));
        // centre = pointer - 22: 218 -> X = 218 - 44 = 174 (no jump at the start); then 179 -> X 135, 3 DIP past the
        // boundary 182 - inside the 4 DIP hysteresis, so the predicted label stays on the origin.
        Assert.Equal("Present(174.0,88.0|2)", port.Effects[^2]);
        Assert.Equal("Present(135.0,88.0|2)", port.Effects[^1]);
    }

    [Fact]
    public void Move_ClampsTheWholeIndicator_WithAnOffCentreGrab()
    {
        var port = new FakePort(Theme, 2);
        var controller = new SegmentDragController(port);
        controller.Press(Mouse(260), 2); // 32 right of the centre
        controller.Move(Mouse(500)); // far right: the pill stays ON the last segment, never past it
        Assert.Equal("Present(184.0,88.0|2)", port.Effects[^1]);

        controller.Move(Mouse(-400)); // far left: exactly the first segment
        Assert.Equal("Present(0.0,88.0|0)", port.Effects[^1]);
    }

    // ---- release ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Release_OverAnotherSegment_CommitsExactlyOnce_AfterTheSessionEnds()
    {
        var (controller, port) = Armed();
        controller.Move(Mouse(200));
        controller.Move(Mouse(136));
        controller.Release(Mouse(136));

        Assert.False(controller.IsActive);
        Assert.Equal(["ReleasePanel(1)", "EndPreview(Committed)", "Commit(1)"], port.Effects.SkipWhile(e => !e.StartsWith("ReleasePanel", StringComparison.Ordinal)));
        Assert.Single(port.Effects, e => e.StartsWith("Commit", StringComparison.Ordinal));
    }

    [Fact]
    public void Release_BackOnTheConfirmedSegment_IsZeroCommits_ButTheSettleIsRequired()
    {
        var (controller, port) = Armed();
        controller.Move(Mouse(150));
        controller.Move(Mouse(226));
        controller.Release(Mouse(226));

        Assert.DoesNotContain(port.Effects, e => e.StartsWith("Commit", StringComparison.Ordinal));
        Assert.Equal(["ReleasePanel(1)", "EndPreview(Returned)"], port.Effects.SkipWhile(e => !e.StartsWith("ReleasePanel", StringComparison.Ordinal)));
    }

    [Fact]
    public void Release_UsesItsOwnPoint_WithoutAFinalMove()
    {
        var (controller, port) = Armed();
        controller.Move(Mouse(220)); // past the threshold, still over the origin
        controller.Release(Mouse(40)); // the release point alone decides

        Assert.Equal("Commit(0)", port.Effects[^1]);
    }

    /// <summary>Prism S0 review §1: release outside the control commits the CLAMPED end segment; clamped on the origin = 0 commits.</summary>
    [Fact]
    public void Release_OutsideTheControl_CommitsTheClampedEnd_OrNothingWhenClampedOnTheOrigin()
    {
        var (left, leftPort) = Armed();
        left.Move(Mouse(200));
        left.Release(Mouse(-61, 47));
        Assert.Equal("Commit(0)", leftPort.Effects[^1]);

        var (right, rightPort) = Armed();
        right.Move(Mouse(260));
        right.Release(Mouse(718, 57));
        Assert.DoesNotContain(rightPort.Effects, e => e.StartsWith("Commit", StringComparison.Ordinal));
        Assert.Equal("EndPreview(Returned)", rightPort.Effects[^1]);
    }

    [Fact]
    public void Release_ReentrantCaptureLost_Checked_AndRemount_CommitExactlyOnce()
    {
        var (controller, port) = Armed();
        // WinUI: releasing the panel raises its CaptureLost; the commit raises Checked (an "external" selection for the
        // adapter) and the theme's remount (Unloaded) - all synchronously, all after the session ended.
        port.OnReleasePanel = id => controller.CaptureLost(id, panelOwned: true);
        port.OnCommit = _ =>
        {
            controller.Cancel(DragCancelReason.ExternalSelection);
            controller.Cancel(DragCancelReason.Unloaded);
            controller.Release(Mouse(136)); // a duplicate release
        };
        controller.Move(Mouse(136));
        controller.Release(Mouse(136));
        controller.CaptureLost(1, panelOwned: true); // and a late one

        Assert.Single(port.Effects, e => e.StartsWith("Commit", StringComparison.Ordinal));
        Assert.Single(port.Effects, e => e.StartsWith("EndPreview", StringComparison.Ordinal));
        Assert.Equal("EndPreview(Committed)", port.Effects.Single(e => e.StartsWith("EndPreview", StringComparison.Ordinal)));
    }

    // ---- cancel -------------------------------------------------------------------------------------------------------

    public static TheoryData<string> CancelPaths() =>
    [
        "Escape", "NavigationKey", "PointerCanceled", "PanelCaptureLost", "WindowDeactivated", "Layout", "ReducedMotionChanged",
        "Disabled", "Unloaded", "ExternalSelection"
    ];

    private static void Cancel(SegmentDragController controller, string path)
    {
        switch (path)
        {
            case "Escape": Assert.True(controller.Key(isEscape: true)); break;
            case "NavigationKey": Assert.False(controller.Key(isEscape: false)); break;
            case "PointerCanceled": controller.Canceled(1); break;
            case "PanelCaptureLost": controller.CaptureLost(1, panelOwned: true); break;
            case "WindowDeactivated": controller.WindowActivation(active: false); break;
            default: controller.Cancel(Enum.Parse<DragCancelReason>(path)); break;
        }
    }

    [Theory]
    [MemberData(nameof(CancelPaths))]
    public void Cancel_WhileDragging_ZeroCommits_ReleasesThePanel_AndAReleaseAfterwardsIsIgnored(string path)
    {
        var (controller, port) = Armed();
        controller.Move(Mouse(136));
        Cancel(controller, path);

        Assert.False(controller.IsActive);
        Assert.Equal(["ReleasePanel(1)", "EndPreview(Cancelled)"], port.Effects.SkipWhile(e => !e.StartsWith("ReleasePanel", StringComparison.Ordinal)));

        controller.Release(Mouse(136)); // the physical release after the cancel
        controller.Move(Mouse(40));
        Assert.DoesNotContain(port.Effects, e => e.StartsWith("Commit", StringComparison.Ordinal));
        Assert.Single(port.Effects, e => e.StartsWith("EndPreview", StringComparison.Ordinal));
    }

    [Fact]
    public void WindowDeactivated_CancelsAPressAndADrag_WithoutAnyCaptureLostOrRelease()
    {
        var (pressed, pressedPort) = Armed();
        pressed.WindowActivation(active: false);
        Assert.False(pressed.IsActive);
        pressed.Move(Mouse(40));
        Assert.DoesNotContain(pressedPort.Effects, e => e.StartsWith("ReleaseOrigin", StringComparison.Ordinal));

        var (dragging, draggingPort) = Armed();
        dragging.Move(Mouse(136));
        dragging.WindowActivation(active: false);
        Assert.Equal("EndPreview(Cancelled)", draggingPort.Effects[^1]);
    }

    [Fact]
    public void AnActivation_NeverCancels_NorResumes()
    {
        var (controller, port) = Armed();
        controller.Move(Mouse(136));
        controller.WindowActivation(active: true);
        Assert.True(controller.IsDragging);

        controller.WindowActivation(active: false);
        controller.WindowActivation(active: true);
        Assert.False(controller.IsActive);
        controller.Release(Mouse(136));
        Assert.DoesNotContain(port.Effects, e => e.StartsWith("Commit", StringComparison.Ordinal));
    }

    [Fact]
    public void Keys_OutsideADrag_AreNeverConsumed()
    {
        var (controller, _) = Armed();

        Assert.False(controller.Key(isEscape: true));
        Assert.True(controller.IsActive); // a press is the item's; Escape does not touch it
    }

    // ---- transfer (Option B) ------------------------------------------------------------------------------------------

    [Fact]
    public void Transfer_TheOriginsOwnCaptureLost_IsExpected_AndTheDragContinues()
    {
        var (controller, port) = Armed();
        port.OnReleaseOrigin = id => controller.CaptureLost(id, panelOwned: false);
        controller.Move(Mouse(136));
        controller.Release(Mouse(136));

        Assert.Equal("Commit(1)", port.Effects[^1]);
    }

    [Theory]
    [InlineData("PointerCanceled")]
    [InlineData("WindowDeactivated")]
    [InlineData("PanelCaptureLost")]
    public void Transfer_ARealConclusionDuringTheOriginsRelease_Terminates_NoCaptureNoPreview(string path)
    {
        var (controller, port) = Armed();
        port.OnReleaseOrigin = _ => Cancel(controller, path);
        controller.Move(Mouse(136));
        controller.Release(Mouse(136));

        Assert.False(controller.IsActive);
        Assert.DoesNotContain(port.Effects, e => e.StartsWith("CapturePanel", StringComparison.Ordinal) || e == "BeginPreview" || e.StartsWith("Commit", StringComparison.Ordinal));
    }

    [Fact]
    public void Transfer_DeactivatedDuringTheCapture_ThatThenSucceeds_ReleasesIt_AndNeverResumes()
    {
        var (controller, port) = Armed();
        port.OnCapturePanel = _ => controller.WindowActivation(active: false);
        controller.Move(Mouse(136));
        controller.Move(Mouse(40));
        controller.Release(Mouse(40));

        Assert.Equal(["ReleaseOrigin(1)", "CapturePanel(1)", "ReleasePanel(1)"], port.Effects.Skip(2));
    }

    [Fact]
    public void Transfer_Refused_IsNoPreview_NoCommit_AndALaterReleaseIsIgnored()
    {
        var (controller, port) = Armed();
        port.CaptureResult = false;
        controller.Move(Mouse(136));
        controller.Release(Mouse(136));

        Assert.False(controller.IsActive);
        Assert.Equal(["ReleaseOrigin(1)", "CapturePanel(1)"], port.Effects.Skip(2));
    }

    // ---- press-phase conclusions --------------------------------------------------------------------------------------

    /// <summary>S0 case 9 (synthetic pen, before V2): Pressed, no Move, the item loses the contact - a safe end, not a drag.</summary>
    [Fact]
    public void StarvedPress_TheItemLosesTheContact_IsASafeEnd_NotADrag()
    {
        var port = new FakePort(Theme, 2);
        var controller = new SegmentDragController(port);
        controller.Press(new DragPointer(5, DragPointerDevice.Pen, 210, 18, true), 2);
        controller.CaptureLost(5, panelOwned: false);
        controller.Move(new DragPointer(5, DragPointerDevice.Pen, 120, 18, true));

        Assert.False(controller.IsActive);
        Assert.Equal(["Snapshot", "Threshold(Pen)"], port.Effects);
    }

    [Fact]
    public void APanelCaptureLost_DuringAPress_IsStale_AndKeepsThePress()
    {
        var (controller, _) = Armed();
        controller.CaptureLost(1, panelOwned: true);

        Assert.True(controller.IsActive);
    }

    // ---- pointers -----------------------------------------------------------------------------------------------------

    [Fact]
    public void OnePointerAtATime_OtherPointersAreIgnored()
    {
        var (controller, port) = Armed();
        controller.Move(Mouse(136));
        controller.Press(Mouse(40, id: 7), 2);
        controller.Move(Mouse(40, id: 7));
        controller.Release(Mouse(40, id: 7));
        controller.Canceled(7);
        controller.CaptureLost(7, panelOwned: true);

        Assert.True(controller.IsDragging);
        controller.Release(Mouse(136));
        Assert.Equal("Commit(1)", port.Effects[^1]);
    }

    [Fact]
    public void TheThreshold_IsTheDevicesThreshold()
    {
        var port = new FakePort(Theme, 2) { ThresholdFor = d => d == DragPointerDevice.Touch ? new DragThreshold(8, 8) : new DragThreshold(4, 4) };
        var controller = new SegmentDragController(port);
        controller.Press(new DragPointer(3, DragPointerDevice.Touch, 228, 16, true), 2);
        controller.Move(new DragPointer(3, DragPointerDevice.Touch, 222, 16, true)); // 6 < 8: still a press

        Assert.False(controller.IsDragging);
        controller.Move(new DragPointer(3, DragPointerDevice.Touch, 220, 16, true));
        Assert.True(controller.IsDragging);
    }

    // ---- geometry -----------------------------------------------------------------------------------------------------

    [Fact]
    public void UnequalWidths_TheWidthInterpolates_AndTheMeasuredMidpointDecides()
    {
        var (controller, port) = Armed(confirmed: 0, segments: Filter);
        controller.Move(Mouse(62 + 77)); // centre at the midpoint between the centres 62 and 216
        Assert.Equal("Present(64.0,150.0|0)", port.Effects[^1]); // width halfway 124 -> 176

        controller.Release(Mouse(62 + 83)); // centre 145: 6 DIP past the boundary 139 -> the second segment
        Assert.Equal("Commit(1)", port.Effects[^1]);
    }

    [Fact]
    public void DisabledSegments_AreNeverTheTarget()
    {
        var (controller, port) = Armed(confirmed: 2, enabled: [true, false, true]);
        controller.Move(Mouse(136));
        controller.Release(Mouse(136)); // over the disabled middle: the nearest ENABLED segment wins (the origin)

        Assert.DoesNotContain(port.Effects, e => e.StartsWith("Commit", StringComparison.Ordinal));
        Assert.Equal("EndPreview(Returned)", port.Effects[^1]);
    }

    private sealed class FakePort(IReadOnlyList<IndicatorRect> segments, int confirmed, bool[]? enabled = null) : ISegmentDragPort
    {
        public List<string> Effects { get; } = [];

        public SegmentDragController? Controller { get; set; }

        public bool GroupEnabled { get; init; } = true;

        public bool CaptureResult { get; set; } = true;

        public Func<DragPointerDevice, DragThreshold> ThresholdFor { get; init; } = _ => new DragThreshold(4, 4);

        public Action<uint>? OnReleaseOrigin { get; set; }

        public Action<uint>? OnCapturePanel { get; set; }

        public Action<uint>? OnReleasePanel { get; set; }

        public Action<int>? OnCommit { get; set; }

        public DragGeometry? Snapshot()
        {
            Effects.Add("Snapshot");
            return new DragGeometry(segments, enabled ?? segments.Select(_ => true).ToArray(), confirmed, GroupEnabled);
        }

        public DragThreshold Threshold(DragPointerDevice device)
        {
            Effects.Add($"Threshold({device})");
            return ThresholdFor(device);
        }

        public void ReleaseOriginCapture(uint pointerId)
        {
            Effects.Add($"ReleaseOrigin({pointerId})");
            OnReleaseOrigin?.Invoke(pointerId);
        }

        public bool CapturePanel(uint pointerId)
        {
            Effects.Add($"CapturePanel({pointerId})");
            OnCapturePanel?.Invoke(pointerId);
            return CaptureResult;
        }

        public void ReleasePanelCapture(uint pointerId)
        {
            Effects.Add($"ReleasePanel({pointerId})");
            OnReleasePanel?.Invoke(pointerId);
        }

        public void BeginPreview() => Effects.Add("BeginPreview");

        public void Present(IndicatorRect rect, int predicted) =>
            Effects.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Present({rect.X:F1},{rect.Width:F1}|{predicted})"));

        public void EndPreview(DragOutcome outcome) => Effects.Add($"EndPreview({outcome})");

        public void Commit(int index)
        {
            Effects.Add($"Commit({index})");
            OnCommit?.Invoke(index);
        }
    }
}
