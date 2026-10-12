namespace ServerMonitor.App.Services.Motion;

/// <summary>The pointer kind of a drag gesture (mirrors Microsoft.UI.Input.PointerDeviceType without the WinUI type).</summary>
public enum DragPointerDevice
{
    Mouse,
    Pen,
    Touch
}

/// <summary>One pointer sample in the indicator host's coordinate space (DIP).</summary>
/// <param name="IsPrimary">Mouse: the left button is down; pen/touch: the contact is down (and, for pen, not the barrel/eraser).</param>
public readonly record struct DragPointer(uint Id, DragPointerDevice Device, double X, double Y, bool IsPrimary);

/// <summary>A drag threshold in DIP, per axis.</summary>
public readonly record struct DragThreshold(double X, double Y);

/// <summary>
/// The measured state of a segmented track at the moment a press arms a drag: every segment's bounds (host space, DIP), which
/// segments are enabled, the confirmed (checked) index and whether the whole group is enabled.
/// </summary>
public sealed record DragGeometry(IReadOnlyList<IndicatorRect> Segments, IReadOnlyList<bool> Enabled, int Confirmed, bool GroupEnabled);

/// <summary>How a drag preview ended (the presenter turns it into the indicator's motion).</summary>
public enum DragOutcome
{
    /// <summary>Released over another enabled segment: ONE commit, then the settle (200 ms Direct; Reduced Motion: snap).</summary>
    Committed,

    /// <summary>Released over the confirmed segment: no write, the settle back (200 ms Direct; Reduced Motion: snap).</summary>
    Returned,

    /// <summary>Escape, capture lost, pointer canceled, window deactivated, a lifecycle change: no write, the selection
    /// animation back to the REAL selection (300 ms Point to Point; Reduced Motion: snap).</summary>
    Cancelled
}

/// <summary>Why a drag (or an armed press) is abandoned.</summary>
public enum DragCancelReason
{
    Escape,
    NavigationKey,
    PointerCanceled,
    CaptureLost,
    WindowDeactivated,
    CaptureRefused,
    Layout,
    ReducedMotionChanged,
    Disabled,
    Unloaded,
    ExternalSelection
}

/// <summary>
/// UI.11.1 H02 (Cortex UI11.1A §2-§3 + S0 review B0-B4/D1, Prism UI11.1A §2-§3, Atlas S0 §2). The effects of a segmented
/// drag on the real control - implemented by the WinUI adapter, faked by the tests. Every call may re-enter the controller
/// synchronously (a capture release raises PointerCaptureLost; a commit raises Checked and, for the theme, a remount).
/// </summary>
public interface ISegmentDragPort
{
    /// <summary>The track as it is measured now, or null when it cannot be measured (not laid out, no selection).</summary>
    DragGeometry? Snapshot();

    /// <summary>The drag threshold for this device at the current DPI (DIP).</summary>
    DragThreshold Threshold(DragPointerDevice device);

    /// <summary>The pressed item lets the pointer go (Option B: no Click is generated afterwards).</summary>
    void ReleaseOriginCapture(uint pointerId);

    /// <summary>The track's panel captures the pointer; false when the platform refuses (B1: the drag is abandoned).</summary>
    bool CapturePanel(uint pointerId);

    /// <summary>The panel lets the pointer go (idempotent).</summary>
    void ReleasePanelCapture(uint pointerId);

    /// <summary>The preview starts: running indicator animations stop and the indicator stops following the planner.</summary>
    void BeginPreview();

    /// <summary>The preview frame: the indicator at <paramref name="rect"/>; the predicted segment's label lit.</summary>
    void Present(IndicatorRect rect, int predicted);

    /// <summary>The preview ends; the indicator goes back to following the planner with the outcome's motion.</summary>
    void EndPreview(DragOutcome outcome);

    /// <summary>The ONE functional write: the segment becomes checked (no Click), and focus follows it if it was in the group.</summary>
    void Commit(int index);
}

/// <summary>UI.11.1 H02 pure geometry and thresholds of the segmented drag (unit-tested; no WinUI types).</summary>
public static class SegmentDragRules
{
    /// <summary>Pen and touch: a design minimum (Cortex §2.3), SM_CXDRAG is defined for the mouse.</summary>
    public const double TouchPenThresholdDip = 8;

    /// <summary>The predicted label changes only this far past a boundary (Prism §2.3, mandatory in High Contrast).</summary>
    public const double HysteresisDip = 4;

    /// <summary>
    /// The drag threshold in DIP. Mouse: the Windows drag rectangle (GetSystemMetricsForDpi SM_CXDRAG / SM_CYDRAG, in
    /// physical pixels at the window DPI) divided by the rasterization scale; a failed metric (0) counts as 1 px. Pen/touch:
    /// at least <see cref="TouchPenThresholdDip"/>.
    /// </summary>
    public static DragThreshold Threshold(DragPointerDevice device, int cxDragPixels, int cyDragPixels, double rasterizationScale)
    {
        var scale = double.IsFinite(rasterizationScale) && rasterizationScale > 0 ? rasterizationScale : 1;
        var x = Math.Max(1, cxDragPixels) / scale;
        var y = Math.Max(1, cyDragPixels) / scale;
        return device == DragPointerDevice.Mouse
            ? new DragThreshold(x, y)
            : new DragThreshold(Math.Max(TouchPenThresholdDip, x), Math.Max(TouchPenThresholdDip, y));
    }

    /// <summary>True when the geometry can carry a drag: at least two finite, non-empty segments and a valid confirmed index.</summary>
    public static bool IsValid(DragGeometry? geometry) =>
        geometry is { Segments.Count: >= 2 } g
        && g.Enabled.Count == g.Segments.Count
        && g.Confirmed >= 0 && g.Confirmed < g.Segments.Count
        && g.Segments.All(r => double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height) && !r.IsEmpty);

    public static double Centre(IndicatorRect rect) => rect.X + rect.Width / 2;

    /// <summary>The indicator's centre kept between the first and the last segment's centres (no rubber band, Prism §3.1).</summary>
    public static double ClampCentre(DragGeometry geometry, double centre) =>
        Math.Clamp(centre, Centre(geometry.Segments[0]), Centre(geometry.Segments[^1]));

    /// <summary>
    /// The preview rectangle for an indicator centre (Prism §3.2): between neighbours i and i+1 the position AND the width
    /// interpolate by the centre's progress between their centres, so the pill always arrives with the target's width.
    /// </summary>
    public static IndicatorRect PresentedRect(DragGeometry geometry, double centre)
    {
        var segments = geometry.Segments;
        centre = ClampCentre(geometry, centre);
        for (var index = 0; index < segments.Count - 1; index++)
        {
            var from = segments[index];
            var to = segments[index + 1];
            var c0 = Centre(from);
            var c1 = Centre(to);
            if (centre <= c1 || index == segments.Count - 2)
            {
                var f = c1 > c0 ? Math.Clamp((centre - c0) / (c1 - c0), 0, 1) : 0;
                var width = Lerp(from.Width, to.Width, f);
                return new IndicatorRect(centre - width / 2, Lerp(from.Y, to.Y, f), width, Lerp(from.Height, to.Height, f));
            }
        }

        return segments[^1];
    }

    /// <summary>
    /// The predicted target for an indicator centre: the ENABLED segment whose range between the midpoints with its enabled
    /// neighbours contains the centre (real geometry, so unequal widths and gaps are handled), kept at <paramref name="current"/>
    /// until the centre is <see cref="HysteresisDip"/> past the boundary. With no enabled segment it is the confirmed one.
    /// </summary>
    public static int Predict(DragGeometry geometry, double centre, int current)
    {
        var eligible = Enumerable.Range(0, geometry.Segments.Count).Where(i => geometry.Enabled[i]).ToList();
        if (eligible.Count == 0)
        {
            return geometry.Confirmed;
        }

        var candidate = eligible[^1];
        for (var k = 0; k < eligible.Count - 1; k++)
        {
            if (centre < Midpoint(geometry, eligible[k], eligible[k + 1]))
            {
                candidate = eligible[k];
                break;
            }
        }

        var position = eligible.IndexOf(current);
        if (position < 0 || candidate == current)
        {
            return candidate;
        }

        if (candidate > current)
        {
            return centre >= Midpoint(geometry, current, eligible[position + 1]) + HysteresisDip ? candidate : current;
        }

        return centre <= Midpoint(geometry, eligible[position - 1], current) - HysteresisDip ? candidate : current;
    }

    private static double Midpoint(DragGeometry geometry, int a, int b) => (Centre(geometry.Segments[a]) + Centre(geometry.Segments[b])) / 2;

    private static double Lerp(double a, double b, double f) => a + (b - a) * f;
}

/// <summary>
/// UI.11.1 H02. The segmented drag-to-select interaction, independent of XAML (Atlas seam): the WinUI adapter feeds it the
/// track's pointer, key, activation and lifecycle events; it decides, and acts only through <see cref="ISegmentDragPort"/>.
/// <list type="bullet">
/// <item>Only a press on the CONFIRMED, enabled segment of an enabled group, with the primary button/contact, arms a drag;
/// anything else stays the item's native click (C1).</item>
/// <item>Below the threshold nothing happens (a click). Mouse: leaving the Windows drag rectangle on EITHER axis starts the
/// drag (SM_CYDRAG semantics; a mouse drag has no system gesture to give back - Prism review P-1). Pen/touch: a vertical move
/// past the threshold first abandons, so the page keeps its vertical pan (S0b). Past the horizontal threshold the capture moves from the item to the panel (Option B, B0) - the phase is marked BEFORE
/// the origin lets go, so the origin's own CaptureLost is expected; a refused capture abandons (B1).</item>
/// <item>The confirmed selection never changes while dragging. Release = at most ONE commit, computed from the release
/// point, and only when the target differs from the confirmation (B4: no Click). The session is terminal BEFORE any effect,
/// so re-entrant CaptureLost / Checked / remount can neither repeat nor undo it.</item>
/// <item>Every other conclusion cancels with zero writes: Escape or a navigation key, PointerCanceled, the panel losing the
/// capture, the window deactivating (D1 - it raises no CaptureLost, measured in S0), a layout/DPI/Reduced Motion change,
/// disabling, unloading, an external selection. One pointer at a time; other pointers are ignored.</item>
/// </list>
/// </summary>
public sealed class SegmentDragController
{
    private enum Phase
    {
        Idle,
        Pressed,
        Transferring,
        Dragging
    }

    private readonly ISegmentDragPort _port;
    private Phase _phase;
    private int _session;
    private uint _pointerId;
    private DragPointerDevice _device;
    private double _pressX;
    private double _pressY;
    private double _grabOffset;
    private DragThreshold _threshold;
    private DragGeometry? _geometry;

    public SegmentDragController(ISegmentDragPort port) => _port = port ?? throw new ArgumentNullException(nameof(port));

    /// <summary>True while a press is armed or a drag runs (anything but Idle).</summary>
    public bool IsActive => _phase != Phase.Idle;

    /// <summary>True while the preview runs (the panel owns the pointer).</summary>
    public bool IsDragging => _phase == Phase.Dragging;

    /// <summary>The predicted segment while dragging, else -1.</summary>
    public int Predicted { get; private set; } = -1;

    /// <summary>Increments whenever a gesture becomes terminal; a stale re-entrant callback compares against it.</summary>
    public int Session => _session;

    /// <param name="pressedIndex">The segment under the press (-1 when the press is not on a segment).</param>
    public void Press(DragPointer pointer, int pressedIndex)
    {
        if (_phase != Phase.Idle || !pointer.IsPrimary)
        {
            return; // one pointer at a time; a secondary button is never a drag
        }

        var geometry = _port.Snapshot();
        if (!SegmentDragRules.IsValid(geometry) || !geometry!.GroupEnabled || pressedIndex != geometry.Confirmed || !geometry.Enabled[pressedIndex])
        {
            return; // a plain click: the item's native behaviour
        }

        _phase = Phase.Pressed;
        _pointerId = pointer.Id;
        _device = pointer.Device;
        _pressX = pointer.X;
        _pressY = pointer.Y;
        _geometry = geometry;
        _grabOffset = pointer.X - SegmentDragRules.Centre(geometry.Segments[geometry.Confirmed]);
        _threshold = _port.Threshold(pointer.Device);
        Predicted = -1;
    }

    public void Move(DragPointer pointer)
    {
        if (_phase == Phase.Idle || pointer.Id != _pointerId)
        {
            return;
        }

        if (_phase == Phase.Pressed)
        {
            var dx = Math.Abs(pointer.X - _pressX);
            var dy = Math.Abs(pointer.Y - _pressY);
            if (dx >= _threshold.X || (_device == DragPointerDevice.Mouse && dy >= _threshold.Y))
            {
                BeginDrag(pointer); // the pill still follows X only
            }
            else if (dy >= _threshold.Y)
            {
                Terminate(); // pen/touch, vertical first: the system (page scroll) keeps the gesture
            }

            return;
        }

        if (_phase == Phase.Dragging)
        {
            Follow(pointer);
        }
    }

    public void Release(DragPointer pointer)
    {
        if (_phase == Phase.Idle || pointer.Id != _pointerId)
        {
            return;
        }

        if (_phase != Phase.Dragging)
        {
            Terminate(); // below the threshold: it was a click, the item handles it
            return;
        }

        Follow(pointer); // the release point decides, even without a final Move
        var geometry = _geometry!;
        var target = Predicted;
        var commit = target != geometry.Confirmed && target >= 0 && geometry.Enabled[target];
        Terminate();
        _port.ReleasePanelCapture(pointer.Id);
        _port.EndPreview(commit ? DragOutcome.Committed : DragOutcome.Returned);
        if (commit)
        {
            _port.Commit(target);
        }
    }

    public void Canceled(uint pointerId)
    {
        if (_phase != Phase.Idle && pointerId == _pointerId)
        {
            Cancel(DragCancelReason.PointerCanceled);
        }
    }

    /// <param name="panelOwned">True when the panel lost the capture; false when an item did.</param>
    public void CaptureLost(uint pointerId, bool panelOwned)
    {
        if (_phase == Phase.Idle || pointerId != _pointerId)
        {
            return;
        }

        switch (_phase)
        {
            case Phase.Transferring when !panelOwned:
                return; // the origin letting go during OUR transfer (B2)
            case Phase.Dragging when !panelOwned:
                return; // a late item notification; the panel owns the drag
            case Phase.Pressed when panelOwned:
                return; // the panel owns nothing during a press: a stale notification of an earlier gesture
            case Phase.Pressed:
                Terminate(); // the item's own conclusion (click release, or the system took the contact): native path
                return;
            default:
                Cancel(DragCancelReason.CaptureLost);
                return;
        }
    }

    /// <summary>Escape cancels a drag (handled); arrows/Tab cancel it first and keep their normal meaning (not handled).</summary>
    /// <returns>True when the key was consumed.</returns>
    public bool Key(bool isEscape)
    {
        if (_phase != Phase.Dragging)
        {
            return false;
        }

        Cancel(isEscape ? DragCancelReason.Escape : DragCancelReason.NavigationKey);
        return isEscape;
    }

    /// <summary>The owning window's activation changed. Only a deactivation cancels; an activation never resumes a gesture.</summary>
    public void WindowActivation(bool active)
    {
        if (!active)
        {
            Cancel(DragCancelReason.WindowDeactivated);
        }
    }

    /// <summary>Any lifecycle reason (layout, DPI, Reduced Motion, disabled, unloaded, external selection): cancel, zero writes.</summary>
    public void Cancel(DragCancelReason reason)
    {
        var phase = _phase;
        if (phase == Phase.Idle)
        {
            return;
        }

        var pointerId = _pointerId;
        Terminate();
        if (phase == Phase.Dragging)
        {
            _port.ReleasePanelCapture(pointerId);
            _port.EndPreview(DragOutcome.Cancelled);
        }

        // Pressed: the item still owns its press - its native click path decides. Transferring: BeginDrag sees the session
        // change and releases whatever it acquired.
    }

    private void BeginDrag(DragPointer pointer)
    {
        _phase = Phase.Transferring; // marked BEFORE the origin lets go (Atlas S0 §2.2)
        var session = _session;
        _port.ReleaseOriginCapture(pointer.Id);
        if (session != _session)
        {
            return; // cancelled re-entrantly (deactivation, canceled) while the origin let go
        }

        var captured = _port.CapturePanel(pointer.Id);
        if (session != _session)
        {
            if (captured)
            {
                _port.ReleasePanelCapture(pointer.Id); // cancelled during the capture: never resume
            }

            return;
        }

        if (!captured)
        {
            Terminate(); // B1: no drag without capture - no preview, no commit
            return;
        }

        _phase = Phase.Dragging;
        _port.BeginPreview();
        if (session == _session)
        {
            Follow(pointer);
        }
    }

    private void Follow(DragPointer pointer)
    {
        var geometry = _geometry!;
        var centre = SegmentDragRules.ClampCentre(geometry, pointer.X - _grabOffset);
        Predicted = SegmentDragRules.Predict(geometry, centre, Predicted < 0 ? geometry.Confirmed : Predicted);
        _port.Present(SegmentDragRules.PresentedRect(geometry, centre), Predicted);
    }

    private void Terminate()
    {
        _phase = Phase.Idle;
        _session++;
        Predicted = -1;
        _geometry = null;
    }
}

/// <summary>
/// UI.11.1 H02 / Cortex S0 D1 (Atlas S0 §2): a window's activation, per window (its AppWindow id, compared by value), with explicit detach. The main window
/// publishes; a drag adapter subscribes for ITS root while loaded, so another window never cancels it and an unloaded track
/// leaves no handler behind. The window deactivating raises no CaptureLost (measured, S0 case 7), so this is the cancel path.
/// </summary>
public sealed class WindowActivationHub
{
    private readonly List<(object Root, Action<bool> Handler)> _subscribers = [];

    public static WindowActivationHub Default { get; } = new();

    /// <summary>How many handlers are attached (QA/test seam).</summary>
    public int Count => _subscribers.Count;

    public IDisposable Subscribe(object root, Action<bool> handler)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(handler);
        var entry = (root, handler);
        _subscribers.Add(entry);
        return new Subscription(this, entry);
    }

    /// <param name="root">The window's key - its AppWindow id (MainWindow publishes <c>AppWindow.Id</c>, the adapter subscribes
    /// with its XamlRoot's <c>ContentIslandEnvironment.AppWindowId</c>), compared by value; null publishes nothing.</param>
    public void Publish(object? root, bool active)
    {
        if (root is null)
        {
            return;
        }

        foreach (var (owner, handler) in _subscribers.ToList())
        {
            if (Equals(owner, root))
            {
                handler(active);
            }
        }
    }

    private sealed class Subscription(WindowActivationHub hub, (object Root, Action<bool> Handler) entry) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                hub._subscribers.Remove(entry);
            }
        }
    }
}
