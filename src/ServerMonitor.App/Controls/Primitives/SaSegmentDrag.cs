using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ServerMonitor.App.Services.Motion;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>What the drag needs from the track's indicator (implemented by <see cref="SlidingSelectionController"/>).</summary>
internal interface ISegmentDragPresenter
{
    IReadOnlyList<RadioButton> Items { get; }

    IndicatorRect Measure(RadioButton item);

    /// <summary>Stops the running indicator animation; the indicator stops following the planner.</summary>
    void BeginPreview();

    /// <summary>One preview frame: direct Composition writes, no animation.</summary>
    void PresentPreview(IndicatorRect rect);

    /// <summary>The preview ended: the indicator follows the planner again, with this cause (DragEnd / DragCancel).</summary>
    void EndPreview(IndicatorCause cause, bool flushNow);
}

/// <summary>
/// UI.11.1 (Atlas review A-1 / AT-03): the real controls of ONE opted-in track, reduced to index-based member accesses with
/// no decision in them, so <see cref="SegmentDragWiring"/> - every decision of the adapter - runs in unit tests against a
/// fake. The production implementation is <see cref="SegmentDragAdapter"/>; each member is one WinUI call.
/// </summary>
internal interface ISegmentDragSurface
{
    int ItemCount { get; }

    IndicatorRect Measure(int index);

    bool IsChecked(int index);

    /// <summary>The item is enabled and visible.</summary>
    bool IsSelectable(int index);

    /// <summary>No ancestor control of the track is disabled.</summary>
    bool IsGroupEnabled { get; }

    double RasterizationScale { get; }

    /// <summary>GetSystemMetricsForDpi(SM_CXDRAG / SM_CYDRAG, dpi), physical pixels.</summary>
    (int Cx, int Cy) DragMetrics(uint dpi);

    void SetItemManipulation(int index, ManipulationModes mode);

    /// <summary><c>items[index].ReleasePointerCapture(pointer)</c>.</summary>
    void ReleaseItemCapture(int index, object pointer);

    /// <summary><c>panel.CapturePointer(pointer)</c>.</summary>
    bool CapturePanel(object pointer);

    /// <summary><c>panel.ReleasePointerCapture(pointer)</c>.</summary>
    void ReleasePanelCapture(object pointer);

    /// <summary><c>DispatcherQueue.TryEnqueue(action)</c>: runs it on a later UI-thread turn, outside the current layout pass.</summary>
    void Post(Action action);

    /// <summary><c>VisualStateManager.GoToState(items[index], lit ? "Checked" : "Unchecked", false)</c>.</summary>
    void ShowCheckState(int index, bool lit);

    /// <summary><c>items[index].IsChecked = true</c>.</summary>
    void Check(int index);

    /// <summary>The window's focused element is inside the track's panel.</summary>
    bool IsFocusInGroup { get; }

    /// <summary><c>items[index].Focus(FocusState.Pointer)</c>.</summary>
    void FocusPointer(int index);

    void BeginPreview();

    void PresentPreview(IndicatorRect rect);

    void EndPreview(IndicatorCause cause, bool flushNow);
}

/// <summary>
/// UI.11.1 H02 - the adapter's decisions between the real controls (<see cref="ISegmentDragSurface"/>) and
/// <see cref="SegmentDragController"/>, with no WinUI object in it:
/// <list type="bullet">
/// <item>Pen and touch (Beacon S0b V2): every opted-in item gets <see cref="ItemManipulation"/> - the ScrollViewer keeps the
/// vertical pan, the horizontal gesture reaches the pointer events. Same path for every device.</item>
/// <item>Option B (Cortex S0 B0): past the threshold the PRESSED item releases the press's pointer and the panel captures
/// it, so the item never raises a Click for the drag (S0 case 5).</item>
/// <item>The ONE write is <c>IsChecked = true</c> on the target - no Click (B4) - and focus follows it when it was in the
/// group, as a pointer focus without a ring (B3).</item>
/// <item>The predicted label (Prism P2, mandatory in High Contrast) shows the Checked visual state on the predicted item and
/// Unchecked on the others while the confirmed IsChecked / view model / UI Automation stay untouched. Cortex review C-1: a
/// label state must never change an item's geometry - every AllowDrag host has width-stable items (guarded by
/// Ui111DragContractTests.EveryAllowDragHost_HasWidthStableItems).</item>
/// <item>B111-1 (Beacon UI.11.1 final, measured under trace): a layout cancel arrives from SizeChanged / XamlRoot.Changed, inside
/// the layout pass of a window resize, and <c>panel.ReleasePointerCapture</c> there fail-fasts WinUI (0xc000027b, E_UNEXPECTED
/// in Microsoft.UI.Xaml.dll). So a layout cancel ends the drag synchronously - no preview write after it - and only the panel's
/// capture release is posted out of the layout pass; it is skipped if a new drag owns the panel by then.</item>
/// </list>
/// </summary>
internal sealed class SegmentDragWiring : ISegmentDragPort
{
    /// <summary>
    /// Beacon S0b verdict V2: the opted-in items' ManipulationMode. <c>System</c> keeps the page's vertical pan (the
    /// ScrollViewer still owns Y); <c>TranslateX</c> keeps the horizontal pen/touch gesture out of DirectManipulation, so it
    /// reaches the pointer events like the mouse (synthetic pen+touch: commit once, cancels zero, taps and scroll unchanged).
    /// The same composition the ToggleSwitch template uses for its thumb. Real pen/touch: NOT_RUN.
    /// </summary>
    internal const ManipulationModes ItemManipulation = ManipulationModes.System | ManipulationModes.TranslateX;

    private readonly ISegmentDragSurface _surface;
    private readonly WindowActivationHub _hub;
    private IDisposable? _activation;
    private object? _pointer;
    private int _origin = -1;
    private int _predicted = -1;
    private bool _committing;
    private bool _inLayout;

    public SegmentDragWiring(ISegmentDragSurface surface, WindowActivationHub? hub = null)
    {
        _surface = surface;
        _hub = hub ?? WindowActivationHub.Default;
        Controller = new SegmentDragController(this);
    }

    /// <summary>QA/test seam: the interaction's state.</summary>
    public SegmentDragController Controller { get; }

    /// <summary>
    /// Loaded (idempotent, a remount raises it again): V2 on the (re)collected items, and ONE subscription to the owning
    /// window's activation (null key = no window known: no subscription).
    /// </summary>
    public void Attach(object? windowKey)
    {
        PrepareItems();
        _activation?.Dispose();
        _activation = windowKey is null ? null : _hub.Subscribe(windowKey, WindowActivation);
    }

    /// <summary>A real unload: cancel, and drop the window subscription.</summary>
    public void Detach()
    {
        Controller.Cancel(DragCancelReason.Unloaded);
        _activation?.Dispose();
        _activation = null;
    }

    /// <summary>The V2 pen/touch arbitration on every collected item.</summary>
    public void PrepareItems()
    {
        for (var index = 0; index < _surface.ItemCount; index++)
        {
            _surface.SetItemManipulation(index, ItemManipulation);
        }
    }

    public void Pressed(DragPointer sample, int index, object pointer)
    {
        if (Controller.IsActive)
        {
            return; // one pointer at a time
        }

        _pointer = pointer;
        _origin = index;
        Controller.Press(sample, index);
    }

    public void Moved(DragPointer sample)
    {
        if (Controller.IsActive)
        {
            Controller.Move(sample);
        }
    }

    public void Released(DragPointer sample)
    {
        if (Controller.IsActive)
        {
            Controller.Release(sample);
        }
    }

    public void Canceled(uint pointerId) => Controller.Canceled(pointerId);

    public void CaptureLost(uint pointerId, bool panelOwned) => Controller.CaptureLost(pointerId, panelOwned);

    /// <summary>Escape cancels a drag and is consumed; arrows/Tab cancel first and keep their meaning.</summary>
    /// <returns>True when the key must be marked handled.</returns>
    public bool Key(bool isEscape) => Controller.Key(isEscape) && isEscape;

    public void WindowActivation(bool active) => Controller.WindowActivation(active);

    /// <summary>A lifecycle cancel; <see cref="DragCancelReason.Layout"/> comes from inside a layout pass (B111-1).</summary>
    public void Cancel(DragCancelReason reason)
    {
        // UI.12 N-3: saved and restored, so a cancel re-entered from inside a layout cancel (still in its layout pass) never
        // clears the flag half-way and releases a capture synchronously inside layout.
        var previous = _inLayout;
        _inLayout = previous || reason == DragCancelReason.Layout;
        try
        {
            Controller.Cancel(reason);
        }
        finally
        {
            _inLayout = previous;
        }
    }

    /// <summary>A Checked/Unchecked the wiring did not write: an external selection wins over the preview.</summary>
    public void OnSelectionChanged()
    {
        if (!_committing)
        {
            Controller.Cancel(DragCancelReason.ExternalSelection);
        }
    }

    // ---- ISegmentDragPort ----------------------------------------------------------------------------------------------

    public DragGeometry? Snapshot()
    {
        var count = _surface.ItemCount;
        if (count < 2)
        {
            return null;
        }

        var rects = Enumerable.Range(0, count).Select(_surface.Measure).ToList();
        // Horizontal tracks only (structural: the vertical sidebar can never drag, even if mis-opted).
        if (rects.Any(r => Math.Abs(r.Y - rects[0].Y) > 0.5 || Math.Abs(r.Height - rects[0].Height) > 0.5))
        {
            return null;
        }

        var confirmed = Enumerable.Range(0, count).FirstOrDefault(_surface.IsChecked, -1);
        return confirmed < 0
            ? null
            : new DragGeometry(rects, Enumerable.Range(0, count).Select(_surface.IsSelectable).ToList(), confirmed, _surface.IsGroupEnabled);
    }

    public DragThreshold Threshold(DragPointerDevice device)
    {
        var scale = _surface.RasterizationScale;
        var dpi = (uint)Math.Round(96 * (double.IsFinite(scale) && scale > 0 ? scale : 1));
        var (cx, cy) = _surface.DragMetrics(dpi);
        return SegmentDragRules.Threshold(device, cx, cy, scale);
    }

    public void ReleaseOriginCapture(uint pointerId)
    {
        if (_origin >= 0 && _pointer is not null)
        {
            _surface.ReleaseItemCapture(_origin, _pointer);
        }
    }

    public bool CapturePanel(uint pointerId) => _pointer is not null && _surface.CapturePanel(_pointer);

    public void ReleasePanelCapture(uint pointerId)
    {
        if (_pointer is not { } pointer)
        {
            return;
        }

        if (!_inLayout)
        {
            _surface.ReleasePanelCapture(pointer);
            return;
        }

        // B111-1: never inside the layout pass. A new drag that owns the panel by then keeps its capture.
        _surface.Post(() =>
        {
            if (!Controller.IsDragging)
            {
                _surface.ReleasePanelCapture(pointer);
            }
        });
    }

    public void BeginPreview()
    {
        _predicted = -1;
        _surface.BeginPreview();
    }

    public void Present(IndicatorRect rect, int predicted)
    {
        _surface.PresentPreview(rect);
        if (predicted != _predicted)
        {
            _predicted = predicted;
            ShowLabels(predicted);
        }
    }

    public void EndPreview(DragOutcome outcome)
    {
        _predicted = -1;
        if (outcome != DragOutcome.Committed)
        {
            ShowLabels(-1); // back to the confirmed states (a commit updates them itself)
        }

        _surface.EndPreview(outcome == DragOutcome.Cancelled ? IndicatorCause.DragCancel : IndicatorCause.DragEnd, flushNow: outcome != DragOutcome.Committed);
    }

    public void Commit(int index)
    {
        if (index < 0 || index >= _surface.ItemCount)
        {
            return;
        }

        var focusInGroup = _surface.IsFocusInGroup;
        _committing = true;
        try
        {
            _surface.Check(index);
        }
        finally
        {
            _committing = false;
        }

        if (focusInGroup && _surface.IsSelectable(index))
        {
            _surface.FocusPointer(index);
        }
    }

    /// <summary>The predicted label lit (Checked state) and every other item Unchecked; -1 = each item's real state.</summary>
    private void ShowLabels(int predicted)
    {
        for (var index = 0; index < _surface.ItemCount; index++)
        {
            _surface.ShowCheckState(index, predicted < 0 ? _surface.IsChecked(index) : index == predicted);
        }
    }
}

/// <summary>
/// UI.11.1 H02 - the WinUI side of the drag on ONE opted-in segmented track (<see cref="SaSlidingSelection.AllowDragProperty"/>):
/// it observes the track's parent panel with handledEventsToo (the RadioButton marks its pointer events handled), turns the
/// routed events into <see cref="SegmentDragWiring"/> inputs, and implements <see cref="ISegmentDragSurface"/> with one
/// WinUI call per member. Every decision lives in the wiring (unit-tested); this class is runtime-proven only (UI.11.1D).
/// </summary>
internal sealed class SegmentDragAdapter : ISegmentDragSurface
{
    private const int SmCxDrag = 68;
    private const int SmCyDrag = 69;

    private readonly FrameworkElement _host;
    private readonly Panel _panel;
    private readonly ISegmentDragPresenter _presenter;

    public SegmentDragAdapter(FrameworkElement host, Panel panel, ISegmentDragPresenter presenter)
    {
        _host = host;
        _panel = panel;
        _presenter = presenter;
        Wiring = new SegmentDragWiring(this);

        // Once per adapter (the panel lives as long as the host): a remount keeps these.
        panel.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), handledEventsToo: true);
        panel.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), handledEventsToo: true);
        panel.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), handledEventsToo: true);
        panel.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnCanceled), handledEventsToo: true);
        panel.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnCaptureLost), handledEventsToo: true);
        panel.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
    }

    /// <summary>QA/test seam: the decisions of this track's drag.</summary>
    public SegmentDragWiring Wiring { get; }

    /// <summary>QA/test seam: the interaction's state.</summary>
    public SegmentDragController Controller => Wiring.Controller;

    /// <summary>Loaded (idempotent): V2 on the (re)collected items; (re)subscribes to the owning window's activation.</summary>
    public void Attach() => Wiring.Attach(_host.XamlRoot?.ContentIslandEnvironment?.AppWindowId);

    /// <summary>Unloaded (a real one - not the stale Unloaded of a remount): cancel, and drop the window subscription.</summary>
    public void Detach() => Wiring.Detach();

    public void Cancel(DragCancelReason reason) => Wiring.Cancel(reason);

    public void OnSelectionChanged() => Wiring.OnSelectionChanged();

    // ---- routed events -------------------------------------------------------------------------------------------------

    private void OnPressed(object sender, PointerRoutedEventArgs e) => Wiring.Pressed(Sample(e), ItemOf(e.OriginalSource), e.Pointer);

    private void OnMoved(object sender, PointerRoutedEventArgs e) => Wiring.Moved(Sample(e));

    private void OnReleased(object sender, PointerRoutedEventArgs e) => Wiring.Released(Sample(e));

    private void OnCanceled(object sender, PointerRoutedEventArgs e) => Wiring.Canceled(e.Pointer.PointerId);

    private void OnCaptureLost(object sender, PointerRoutedEventArgs e) =>
        Wiring.CaptureLost(e.Pointer.PointerId, panelOwned: ReferenceEquals(e.OriginalSource, _panel));

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var key = e.Key;
        if (key == Windows.System.VirtualKey.Escape)
        {
            if (Wiring.Key(isEscape: true))
            {
                e.Handled = true;
            }
        }
        else if (key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Up
                 or Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Tab)
        {
            Wiring.Key(isEscape: false); // cancel first; the key keeps its normal meaning
        }
    }

    private DragPointer Sample(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_host);
        var device = point.PointerDeviceType switch
        {
            PointerDeviceType.Pen => DragPointerDevice.Pen,
            PointerDeviceType.Touch => DragPointerDevice.Touch,
            _ => DragPointerDevice.Mouse
        };
        var properties = point.Properties;
        var primary = device switch
        {
            DragPointerDevice.Mouse => properties.IsLeftButtonPressed,
            DragPointerDevice.Pen => point.IsInContact && !properties.IsBarrelButtonPressed && !properties.IsEraser,
            _ => point.IsInContact
        };
        return new DragPointer(e.Pointer.PointerId, device, point.Position.X, point.Position.Y, primary);
    }

    private int ItemOf(object source)
    {
        var items = _presenter.Items;
        for (var node = source as DependencyObject; node is not null && !ReferenceEquals(node, _panel); node = VisualTreeHelper.GetParent(node))
        {
            if (node is RadioButton radio)
            {
                for (var index = 0; index < items.Count; index++)
                {
                    if (ReferenceEquals(items[index], radio))
                    {
                        return index;
                    }
                }

                return -1;
            }
        }

        return -1;
    }

    // ---- ISegmentDragSurface: one WinUI call each ----------------------------------------------------------------------

    public int ItemCount => _presenter.Items.Count;

    public IndicatorRect Measure(int index) => _presenter.Measure(_presenter.Items[index]);

    public bool IsChecked(int index) => _presenter.Items[index].IsChecked == true;

    public bool IsSelectable(int index) => _presenter.Items[index] is { IsEnabled: true, Visibility: Visibility.Visible };

    public bool IsGroupEnabled
    {
        get
        {
            for (DependencyObject? node = _panel; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is Control { IsEnabled: false })
                {
                    return false;
                }
            }

            return true;
        }
    }

    public double RasterizationScale => _host.XamlRoot?.RasterizationScale ?? 1;

    public (int Cx, int Cy) DragMetrics(uint dpi) => (GetSystemMetricsForDpi(SmCxDrag, dpi), GetSystemMetricsForDpi(SmCyDrag, dpi));

    public void SetItemManipulation(int index, ManipulationModes mode) => _presenter.Items[index].ManipulationMode = mode;

    public void ReleaseItemCapture(int index, object pointer) => _presenter.Items[index].ReleasePointerCapture((Pointer)pointer);

    public bool CapturePanel(object pointer) => _panel.CapturePointer((Pointer)pointer);

    public void ReleasePanelCapture(object pointer) => _panel.ReleasePointerCapture((Pointer)pointer);

    public void Post(Action action) => _panel.DispatcherQueue.TryEnqueue(() => action());

    public void ShowCheckState(int index, bool lit) => VisualStateManager.GoToState(_presenter.Items[index], lit ? "Checked" : "Unchecked", false);

    public void Check(int index) => _presenter.Items[index].IsChecked = true;

    public bool IsFocusInGroup
    {
        get
        {
            if (_host.XamlRoot is not { } root || FocusManager.GetFocusedElement(root) is not DependencyObject focused)
            {
                return false;
            }

            for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, _panel))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void FocusPointer(int index) => _presenter.Items[index].Focus(FocusState.Pointer);

    public void BeginPreview() => _presenter.BeginPreview();

    public void PresentPreview(IndicatorRect rect) => _presenter.PresentPreview(rect);

    public void EndPreview(IndicatorCause cause, bool flushNow) => _presenter.EndPreview(cause, flushNow);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
