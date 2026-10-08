using ServerMonitor.App.Views;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.8 micro-fix c3, Beacon c2 B-6 (WCAG 2.4.3). MEASURED (DIAG launch at fccfada, n20, list scrolled to row 15, Expandir →
/// Modo compacto): the scroll position is kept, the repeater realizes rows 3-19 only - row 1 is never realized - and the
/// LayoutUpdated passes stop after pass 5, so the wait for "row 1" sat at Wait below the 8-pass fallback: focus stayed on
/// an unnamed Pane. Now: the target is the first row VISIBLE in the viewport (the scroll position is kept), and every
/// waiting outcome requests the next layout pass, so the bounded fallback is always reached. Deterministic: no WinUI, no
/// timer - layout passes are driven by the test, only when requested.
/// </summary>
public sealed class Ui8CompactC3Tests
{
    private const double Step = 88; // row 80 + gap 8 (Ui8CompactC1Tests)

    // ---- the target: the first VISIBLE row among the realized ones ----

    [Fact]
    public void Scrolled_TheFirstVisibleRowIsChosen_NotRow1_AndNotACachedRowAboveTheViewport()
    {
        // As measured: realized 3..19, the list scrolled so row 10 starts 4 px into the viewport (rows 3..9 are cache above).
        var realized = Enumerable.Range(3, 17).Select(index => (index, Top: (index - 10) * Step + 4, Height: 80.0)).ToList();

        Assert.Equal(10, CompactEntryFocus.FirstVisibleRow(realized, viewportHeight: 520));
    }

    [Fact]
    public void APartlyScrolledRowAtTheTop_GivesWayToTheFirstWholeRowStart()
    {
        (int, double, double)[] realized = [(9, -40, 80), (10, 48, 80), (11, 136, 80)];

        Assert.Equal(10, CompactEntryFocus.FirstVisibleRow(realized, viewportHeight: 520));
    }

    [Fact]
    public void AtTheTop_Row1IsTheFirstVisible_AndTheInputOrderDoesNotMatter()
    {
        (int, double, double)[] realized = [(2, 176, 80), (0, 0, 80), (1, 88, 80)];

        Assert.Equal(0, CompactEntryFocus.FirstVisibleRow(realized, viewportHeight: 520));
    }

    [Fact]
    public void OnlyAnOverlappingRow_IsStillVisible()
    {
        (int, double, double)[] realized = [(4, -100, 300)];

        Assert.Equal(4, CompactEntryFocus.FirstVisibleRow(realized, viewportHeight: 150));
    }

    [Fact]
    public void NothingInView_OrNoViewportYet_IsNoTarget()
    {
        (int, double, double)[] above = [(1, -200, 80), (2, -112, 80)];
        (int, double, double)[] below = [(18, 600, 80)];
        (int, double, double)[] unmeasured = [(0, 0, 0)];

        Assert.Null(CompactEntryFocus.FirstVisibleRow(above, viewportHeight: 520));
        Assert.Null(CompactEntryFocus.FirstVisibleRow(below, viewportHeight: 520));
        Assert.Null(CompactEntryFocus.FirstVisibleRow(unmeasured, viewportHeight: 520));
        Assert.Null(CompactEntryFocus.FirstVisibleRow([(0, 0, 80)], viewportHeight: 0));
    }

    // ---- the wait always ends: each waiting outcome requests the next pass ----

    [Fact]
    public void ALayoutThatHasSettled_StillReachesTheFallback_BecauseEveryWaitRequestsTheNextPass()
    {
        var layout = new SettledLayout();
        var targets = new List<CompactEntryFocus.Target>();
        var wait = new CompactEntryFocusWait(layout.Add, layout.Remove, passes =>
        {
            // Rows exist, none ever becomes visible (the B-6 shape before the target fix).
            var target = CompactEntryFocus.Decide(hasRows: true, visibleRowReady: false, hasAction: false, actionReady: false, passes);
            targets.Add(target);
            return target != CompactEntryFocus.Target.Wait;
        }, layout.Request);

        wait.Begin();
        layout.RunRequestedPasses();

        Assert.Equal(CompactEntryFocus.MaxLayoutPasses + 1, targets.Count);
        Assert.Equal(CompactEntryFocus.Target.Expand, targets[^1]);
        Assert.Equal(CompactEntryFocus.MaxLayoutPasses, layout.Requests);
        Assert.False(wait.IsWaiting);
        Assert.Equal(0, layout.Handlers);
    }

    [Fact]
    public void AVisibleRowThatArrivesOnARequestedPass_IsTheTarget_AndNothingMoreIsRequested()
    {
        var layout = new SettledLayout();
        var targets = new List<CompactEntryFocus.Target>();
        var wait = new CompactEntryFocusWait(layout.Add, layout.Remove, passes =>
        {
            var target = CompactEntryFocus.Decide(hasRows: true, visibleRowReady: passes >= 2, hasAction: false, actionReady: false, passes);
            targets.Add(target);
            return target != CompactEntryFocus.Target.Wait;
        }, layout.Request);

        wait.Begin();
        layout.RunRequestedPasses();

        Assert.Equal([CompactEntryFocus.Target.Wait, CompactEntryFocus.Target.Wait, CompactEntryFocus.Target.FirstVisibleRow], targets);
        Assert.Equal(2, layout.Requests);
        Assert.False(wait.IsWaiting);
    }

    // ---- the window and the shell use them ----

    [Fact]
    public void TheWindow_FocusesTheShellsFirstVisibleRow_AndLetsTheWaitRequestLayoutPasses()
    {
        var window = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        Assert.Contains("() => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, CompactShellView.InvalidateMeasure));", window, StringComparison.Ordinal);
        Assert.Contains("var visibleRow = hasRows ? CompactShellView.FirstVisibleRow() : null;", window, StringComparison.Ordinal);
        Assert.Contains("visibleRow!.Focus(_compactEntryFocusState);", window, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetElement(0)", window, StringComparison.Ordinal);

        var shell = AppSourceTree.CodeWithoutComments("Controls/CompactShell.xaml.cs");
        Assert.Contains("row.TransformToVisual(ListScroller)", shell, StringComparison.Ordinal);
        Assert.Contains("Views.CompactEntryFocus.FirstVisibleRow(realized, ListScroller.ViewportHeight)", shell, StringComparison.Ordinal);
    }

    /// <summary>A layout system that has SETTLED: it runs a pass only when one was requested (as InvalidateMeasure does).</summary>
    private sealed class SettledLayout
    {
        private readonly List<EventHandler<object>> _handlers = [];
        private int _pending;

        public int Handlers => _handlers.Count;

        public int Requests { get; private set; }

        public void Add(EventHandler<object> handler) => _handlers.Add(handler);

        public void Remove(EventHandler<object> handler) => _handlers.Remove(handler);

        public void Request()
        {
            Requests++;
            _pending++;
        }

        public void RunRequestedPasses()
        {
            for (var guard = 0; _pending > 0 && guard < 100; guard++)
            {
                _pending--;
                foreach (var handler in _handlers.ToList())
                {
                    handler(null, new object());
                }
            }
        }
    }
}
