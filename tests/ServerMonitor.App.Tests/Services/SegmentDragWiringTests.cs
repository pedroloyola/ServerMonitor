using System.Globalization;
using Microsoft.UI.Xaml.Input;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services.Motion;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// UI.11.1 (Atlas review A-1 / AT-03). The production adapter decisions (<see cref="SegmentDragWiring"/>) driven against a
/// recording fake of the real controls (<see cref="ISegmentDragSurface"/>, one WinUI call per member in production): V2 is
/// applied to every item on attach, the PRESSED item releases the PRESS's pointer before the panel captures it, the labels,
/// the commit and its focus, the window subscription. Only the one-call surface members stay runtime-only (UI.11.1D).
/// </summary>
public sealed class SegmentDragWiringTests
{
    private static readonly IndicatorRect[] Theme = [new(0, 4, 88, 28), new(92, 4, 88, 28), new(184, 4, 88, 28)];

    private sealed class FakeSurface(IndicatorRect[] rects, int confirmed) : ISegmentDragSurface
    {
        private readonly bool[] _checked = rects.Select((_, i) => i == confirmed).ToArray();

        public List<string> Log { get; } = [];

        public bool[] Selectable { get; init; } = rects.Select(_ => true).ToArray();

        public bool FocusInGroup { get; set; } = true;

        public bool CaptureResult { get; set; } = true;

        public double Scale { get; set; } = 1;

        public Action? OnCheck { get; set; }

        public int ItemCount => rects.Length;

        public IndicatorRect Measure(int index) => rects[index];

        public bool IsChecked(int index) => _checked[index];

        public bool IsSelectable(int index) => Selectable[index];

        public bool IsGroupEnabled { get; set; } = true;

        public double RasterizationScale => Scale;

        public (int Cx, int Cy) DragMetrics(uint dpi)
        {
            Log.Add($"DragMetrics({dpi})");
            return ((int)Math.Round(4 * dpi / 96.0), (int)Math.Round(4 * dpi / 96.0));
        }

        public void SetItemManipulation(int index, ManipulationModes mode) => Log.Add($"Manipulation({index},{mode})");

        public void ReleaseItemCapture(int index, object pointer) => Log.Add($"ReleaseItemCapture({index},{pointer})");

        public bool CapturePanel(object pointer)
        {
            Log.Add($"CapturePanel({pointer})");
            return CaptureResult;
        }

        public void ReleasePanelCapture(object pointer) => Log.Add($"ReleasePanelCapture({pointer})");

        public Queue<Action> Posted { get; } = new();

        public void Post(Action action)
        {
            Log.Add("Post");
            Posted.Enqueue(action);
        }

        public void RunPosted()
        {
            while (Posted.TryDequeue(out var action))
            {
                action();
            }
        }

        public void ShowCheckState(int index, bool lit) => Log.Add($"State({index},{(lit ? "Checked" : "Unchecked")})");

        public void Check(int index)
        {
            Log.Add($"Check({index})");
            for (var i = 0; i < _checked.Length; i++)
            {
                _checked[i] = i == index;
            }

            OnCheck?.Invoke(); // WinUI raises Checked synchronously inside the setter
        }

        public bool IsFocusInGroup => FocusInGroup;

        public void FocusPointer(int index) => Log.Add($"FocusPointer({index})");

        public void BeginPreview() => Log.Add("BeginPreview");

        public void PresentPreview(IndicatorRect rect) => Log.Add(string.Create(CultureInfo.InvariantCulture, $"Present({rect.X:F1})"));

        public void EndPreview(IndicatorCause cause, bool flushNow)
        {
            Log.Add($"EndPreview({cause},{flushNow})");
            var reentry = OnEndPreview;
            OnEndPreview = null; // once
            reentry?.Invoke();
        }

        /// <summary>UI.12 N-3: work re-entered from inside a cancel's own surface call - still inside its layout pass (fires once).</summary>
        public Action? OnEndPreview { get; set; }
    }

    private static DragPointer Mouse(double x, double y = 16, uint id = 1) => new(id, DragPointerDevice.Mouse, x, y, true);

    private static (SegmentDragWiring Wiring, FakeSurface Surface) Track(int confirmed = 2)
    {
        var surface = new FakeSurface(Theme, confirmed);
        return (new SegmentDragWiring(surface, new WindowActivationHub()), surface);
    }

    // ---- V2 (A-1) -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Attach_AppliesV2ToEveryItem()
    {
        var (wiring, surface) = Track();
        wiring.Attach(windowKey: null);

        Assert.Equal(
            Enumerable.Range(0, 3).Select(i => $"Manipulation({i},{ManipulationModes.System | ManipulationModes.TranslateX})"),
            surface.Log);
    }

    [Fact]
    public void Attach_AgainAfterARemount_ReappliesV2_AndKeepsOneWindowSubscription()
    {
        var hub = new WindowActivationHub();
        var surface = new FakeSurface(Theme, 2);
        var wiring = new SegmentDragWiring(surface, hub);

        wiring.Attach(windowKey: 7UL);
        wiring.Attach(windowKey: 7UL);

        Assert.Equal(6, surface.Log.Count(e => e.StartsWith("Manipulation(", StringComparison.Ordinal)));
        Assert.Equal(1, hub.Count);
        wiring.Detach();
        Assert.Equal(0, hub.Count);
    }

    [Fact]
    public void TheWindowSubscription_CancelsADrag_OnlyForItsOwnWindow()
    {
        var hub = new WindowActivationHub();
        var surface = new FakeSurface(Theme, 2);
        var wiring = new SegmentDragWiring(surface, hub);
        wiring.Attach(windowKey: 7UL);
        wiring.Pressed(Mouse(228), 2, "P1");
        wiring.Moved(Mouse(136));

        hub.Publish(8UL, false);
        Assert.True(wiring.Controller.IsDragging);

        hub.Publish(7UL, false);
        Assert.False(wiring.Controller.IsActive);
        Assert.Contains("EndPreview(DragCancel,True)", surface.Log);
    }

    // ---- Option B transfer (AT-03) ------------------------------------------------------------------------------------

    [Fact]
    public void PastTheThreshold_ThePressedItemReleasesThePressPointer_BeforeThePanelCapturesIt()
    {
        var (wiring, surface) = Track();
        wiring.Pressed(Mouse(228), 2, "P1");
        wiring.Moved(Mouse(224));

        Assert.Equal(["DragMetrics(96)", "ReleaseItemCapture(2,P1)", "CapturePanel(P1)", "BeginPreview"], surface.Log.Take(4));
    }

    [Fact]
    public void ASecondPress_NeverReplacesThePointerOrTheOrigin()
    {
        var (wiring, surface) = Track();
        wiring.Pressed(Mouse(228), 2, "P1");
        wiring.Pressed(Mouse(40, id: 9), 0, "P9");
        wiring.Moved(Mouse(136));
        wiring.Released(Mouse(136));

        Assert.Contains("ReleaseItemCapture(2,P1)", surface.Log);
        Assert.Contains("ReleasePanelCapture(P1)", surface.Log);
        Assert.DoesNotContain(surface.Log, e => e.Contains("P9", StringComparison.Ordinal));
    }

    [Fact]
    public void APressOutsideTheItems_NeverReleasesAnItemCapture()
    {
        var (wiring, surface) = Track();
        wiring.Pressed(Mouse(228), -1, "P1");
        wiring.Moved(Mouse(136));

        Assert.DoesNotContain(surface.Log, e => e.StartsWith("ReleaseItemCapture", StringComparison.Ordinal));
    }

    [Fact]
    public void ARefusedPanelCapture_NoPreview()
    {
        var (wiring, surface) = Track();
        surface.CaptureResult = false;
        wiring.Pressed(Mouse(228), 2, "P1");
        wiring.Moved(Mouse(136));

        Assert.DoesNotContain("BeginPreview", surface.Log);
        Assert.False(wiring.Controller.IsActive);
    }

    // ---- threshold from the real metrics ------------------------------------------------------------------------------

    [Fact]
    public void TheThreshold_UsesTheMetricsAtTheWindowDpi()
    {
        var (wiring, surface) = Track();
        surface.Scale = 1.5;
        wiring.Pressed(Mouse(228), 2, "P1");
        wiring.Moved(Mouse(228 - 3.9)); // 6 px at 144 DPI = 4 DIP: still a press

        Assert.Equal("DragMetrics(144)", surface.Log.Single(e => e.StartsWith("DragMetrics", StringComparison.Ordinal)));
        Assert.False(wiring.Controller.IsDragging);
    }

    // ---- labels, commit, focus ----------------------------------------------------------------------------------------

    [Fact]
    public void ThePredictedLabel_IsLitThroughTheCheckStates_AndRestoredOnReturn()
    {
        var (wiring, surface) = Track();
        wiring.Pressed(Mouse(228), 2, "P1");
        wiring.Moved(Mouse(224));
        wiring.Moved(Mouse(136));
        wiring.Moved(Mouse(228));
        wiring.Released(Mouse(228));

        var states = surface.Log.Where(e => e.StartsWith("State(", StringComparison.Ordinal)).ToList();
        Assert.Equal(
        [
            "State(0,Unchecked)", "State(1,Unchecked)", "State(2,Checked)",   // the origin, first frame
            "State(0,Unchecked)", "State(1,Checked)", "State(2,Unchecked)",   // predicted: the middle one
            "State(0,Unchecked)", "State(1,Unchecked)", "State(2,Checked)",   // back over the origin
            "State(0,Unchecked)", "State(1,Unchecked)", "State(2,Checked)"    // released: the real states
        ], states);
        Assert.DoesNotContain(surface.Log, e => e.StartsWith("Check(", StringComparison.Ordinal));
        Assert.Equal("EndPreview(DragEnd,True)", surface.Log[^1]);
    }

    [Fact]
    public void Commit_ChecksTheTargetOnce_AndMovesAPointerFocusToIt_WhenFocusWasInTheGroup()
    {
        var (wiring, surface) = Track();
        wiring.Pressed(Mouse(228), 2, "P1");
        wiring.Moved(Mouse(136));
        wiring.Released(Mouse(136));

        Assert.Equal(["ReleasePanelCapture(P1)", "EndPreview(DragEnd,False)", "Check(1)", "FocusPointer(1)"],
            surface.Log.SkipWhile(e => !e.StartsWith("ReleasePanelCapture", StringComparison.Ordinal)));
    }

    [Fact]
    public void Commit_NeverPullsFocusIntoTheGroup()
    {
        var (wiring, surface) = Track();
        surface.FocusInGroup = false;
        wiring.Pressed(Mouse(228), 2, "P1");
        wiring.Moved(Mouse(136));
        wiring.Released(Mouse(136));

        Assert.Contains("Check(1)", surface.Log);
        Assert.DoesNotContain(surface.Log, e => e.StartsWith("FocusPointer", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCommitsOwnChecked_IsNotAnExternalSelection_ButAnyOtherIs()
    {
        var (wiring, surface) = Track();
        surface.OnCheck = wiring.OnSelectionChanged; // the commit's own Checked, re-entrant
        wiring.Pressed(Mouse(228), 2, "P1");
        wiring.Moved(Mouse(136));
        wiring.Released(Mouse(136));
        Assert.Single(surface.Log, e => e.StartsWith("EndPreview", StringComparison.Ordinal));

        wiring.Pressed(Mouse(136), 1, "P2");
        wiring.Moved(Mouse(40));
        wiring.OnSelectionChanged(); // e.g. a keyboard or view-model selection while dragging
        Assert.False(wiring.Controller.IsActive);
        Assert.Equal("EndPreview(DragCancel,True)", surface.Log.Last(e => e.StartsWith("EndPreview", StringComparison.Ordinal)));
    }

    // ---- snapshot -----------------------------------------------------------------------------------------------------

    [Fact]
    public void AVerticalTrack_NeverArms()
    {
        var surface = new FakeSurface([new(0, 0, 200, 40), new(0, 52, 200, 40)], 0);
        var wiring = new SegmentDragWiring(surface, new WindowActivationHub());
        wiring.Pressed(Mouse(100, 20), 0, "P1");

        Assert.False(wiring.Controller.IsActive);
    }

    [Fact]
    public void ADisabledGroup_OrConfirmedItem_NeverArms()
    {
        var group = new FakeSurface(Theme, 2) { IsGroupEnabled = false };
        var groupWiring = new SegmentDragWiring(group, new WindowActivationHub());
        groupWiring.Pressed(Mouse(228), 2, "P1");
        Assert.False(groupWiring.Controller.IsActive);

        var item = new FakeSurface(Theme, 2) { Selectable = [true, true, false] };
        var itemWiring = new SegmentDragWiring(item, new WindowActivationHub());
        itemWiring.Pressed(Mouse(228), 2, "P1");
        Assert.False(itemWiring.Controller.IsActive);
    }

    // ---- keys and lifecycle -------------------------------------------------------------------------------------------

    [Fact]
    public void Escape_IsConsumedOnlyByADrag_NavigationKeysNever()
    {
        var (wiring, _) = Track();
        Assert.False(wiring.Key(isEscape: true));

        wiring.Pressed(Mouse(228), 2, "P1");
        wiring.Moved(Mouse(136));
        Assert.True(wiring.Key(isEscape: true));

        wiring.Pressed(Mouse(228), 2, "P2");
        wiring.Moved(Mouse(136));
        Assert.False(wiring.Key(isEscape: false));
        Assert.False(wiring.Controller.IsActive);
    }

    [Fact]
    public void Detach_CancelsARunningDrag()
    {
        var (wiring, surface) = Track();
        wiring.Pressed(Mouse(228), 2, "P1");
        wiring.Moved(Mouse(136));
        wiring.Detach();

        Assert.Equal(["ReleasePanelCapture(P1)", "EndPreview(DragCancel,True)"], surface.Log.TakeLast(5).Where(e => !e.StartsWith("State", StringComparison.Ordinal)));
    }

    // ---- B111-1: a layout cancel comes from inside the layout pass ----------------------------------------------------

    [Fact]
    public void ALayoutCancel_EndsTheDragNow_ButReleasesThePanelCaptureOutsideTheLayoutPass()
    {
        var (wiring, surface) = Track();
        wiring.Pressed(Mouse(228), 2, "P1");
        wiring.Moved(Mouse(136));
        surface.Log.Clear();

        wiring.Cancel(DragCancelReason.Layout); // raised by SizeChanged during a window resize

        Assert.False(wiring.Controller.IsActive);
        Assert.DoesNotContain("ReleasePanelCapture(P1)", surface.Log); // the call that fail-fasts WinUI inside layout
        Assert.Equal(["Post", "EndPreview(DragCancel,True)"], surface.Log.Where(e => !e.StartsWith("State", StringComparison.Ordinal)));

        wiring.Moved(Mouse(100)); // the panel still holds the pointer until the post runs: no preview write
        Assert.DoesNotContain(surface.Log, e => e.StartsWith("Present", StringComparison.Ordinal));

        surface.RunPosted();
        Assert.Equal("ReleasePanelCapture(P1)", surface.Log[^1]);
    }

    [Fact]
    public void ALayoutCancelsPostedRelease_LeavesANewDragsCapture_AndOtherCancelsStaySynchronous()
    {
        var (wiring, surface) = Track();
        wiring.Pressed(Mouse(228), 2, "P1");
        wiring.Moved(Mouse(136));
        wiring.Cancel(DragCancelReason.Layout);
        wiring.Pressed(Mouse(228), 2, "P2");
        wiring.Moved(Mouse(136));
        surface.Log.Clear();

        surface.RunPosted();
        Assert.Empty(surface.Log);
        Assert.True(wiring.Controller.IsDragging);

        wiring.Cancel(DragCancelReason.Disabled);
        Assert.Equal(["ReleasePanelCapture(P2)", "EndPreview(DragCancel,True)"], surface.Log.Where(e => !e.StartsWith("State", StringComparison.Ordinal)));
        Assert.Empty(surface.Posted);
    }

    /// <summary>
    /// UI.12 N-3 (Cortex UI.12A C-W5): a Layout cancel runs inside the layout pass. Work re-entered from inside it (here: from
    /// its EndPreview, still in the pass) that starts a new drag and cancels it for a NON-layout reason used to reset the
    /// "in layout" flag to that reason, releasing the new drag's panel capture synchronously inside the layout pass - the
    /// call that fail-fasts WinUI (B111-1). The flag is now saved and restored: inside a layout cancel, every release is posted.
    /// </summary>
    [Fact]
    public void ACancelReEnteredInsideALayoutCancel_StillPostsItsPanelRelease()
    {
        var (wiring, surface) = Track();
        wiring.Pressed(Mouse(228), 2, "P1");
        wiring.Moved(Mouse(136));
        surface.OnEndPreview = () =>
        {
            wiring.Pressed(Mouse(228), 2, "P2");
            wiring.Moved(Mouse(136));
            wiring.Cancel(DragCancelReason.Disabled);
        };
        surface.Log.Clear();

        wiring.Cancel(DragCancelReason.Layout);

        Assert.Null(surface.OnEndPreview); // the re-entry did happen
        Assert.DoesNotContain(surface.Log, e => e.StartsWith("ReleasePanelCapture", StringComparison.Ordinal));
        Assert.Equal(2, surface.Log.Count(e => e == "Post"));

        surface.RunPosted();
        Assert.Contains("ReleasePanelCapture(P1)", surface.Log);
        Assert.Contains("ReleasePanelCapture(P2)", surface.Log);

        // Outside any layout cancel, a non-layout cancel stays synchronous (the flag was restored, not left set).
        wiring.Pressed(Mouse(228), 2, "P3");
        wiring.Moved(Mouse(136));
        surface.Log.Clear();
        wiring.Cancel(DragCancelReason.Disabled);
        Assert.Equal("ReleasePanelCapture(P3)", surface.Log.First(e => !e.StartsWith("State", StringComparison.Ordinal)));
    }
}
