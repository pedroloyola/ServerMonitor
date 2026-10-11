using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ServerMonitor.App.Services.Motion;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>What the drag adapter needs from the track's indicator (implemented by <see cref="SlidingSelectionController"/>).</summary>
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
/// UI.11.1 H02 - the WinUI adapter of <see cref="SegmentDragController"/> on ONE opted-in segmented track
/// (<see cref="SaSlidingSelection.AllowDragProperty"/>). It observes the track's parent panel with handledEventsToo (the
/// RadioButton marks its pointer events handled), turns pointer/key/activation/lifecycle events into controller inputs, and
/// implements the controller's effects on the real controls:
/// <list type="bullet">
/// <item>Option B (Cortex S0 B0): past the threshold the pressed item releases the pointer and the PANEL captures it, so the
/// item never raises a Click for the drag (S0 case 5) and the panel keeps the gesture outside the control and the window.</item>
/// <item>The ONE write is <c>IsChecked = true</c> on the target - no Click (B4) - and focus follows it when it was in the group,
/// as a pointer focus without a ring (B3).</item>
/// <item>The predicted label (Prism P2, mandatory in High Contrast) shows the Checked visual state on the predicted item and
/// Unchecked on the others while the confirmed IsChecked / view model / UI Automation stay untouched.</item>
/// <item>Pen and touch (Beacon S0b V2): the opted-in items carry <c>ManipulationMode = System, TranslateX</c> - the
/// ScrollViewer keeps the vertical pan, the horizontal gesture reaches the pointer events. Same path for every device.</item>
/// </list>
/// </summary>
internal sealed class SegmentDragAdapter : ISegmentDragPort
{
    private const int SmCxDrag = 68;
    private const int SmCyDrag = 69;

    private readonly FrameworkElement _host;
    private readonly Panel _panel;
    private readonly ISegmentDragPresenter _presenter;
    private readonly SegmentDragController _controller;
    private IDisposable? _activation;
    private Pointer? _pointer;
    private RadioButton? _origin;
    private int _predicted = -1;
    private bool _committing;

    public SegmentDragAdapter(FrameworkElement host, Panel panel, ISegmentDragPresenter presenter)
    {
        _host = host;
        _panel = panel;
        _presenter = presenter;
        _controller = new SegmentDragController(this);

        // Once per adapter (the panel lives as long as the host): a remount keeps these.
        panel.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), handledEventsToo: true);
        panel.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), handledEventsToo: true);
        panel.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), handledEventsToo: true);
        panel.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnCanceled), handledEventsToo: true);
        panel.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnCaptureLost), handledEventsToo: true);
        panel.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);
    }

    /// <summary>QA/test seam: the interaction's state.</summary>
    public SegmentDragController Controller => _controller;

    /// <summary>Loaded (idempotent): (re)subscribes to the owning window's activation.</summary>
    public void Attach()
    {
        _activation?.Dispose();
        _activation = _host.XamlRoot?.ContentIslandEnvironment?.AppWindowId is { } window
            ? WindowActivationHub.Default.Subscribe(window, OnWindowActivation)
            : null;
    }

    /// <summary>Unloaded (a real one - not the stale Unloaded of a remount): cancel, and drop the window subscription.</summary>
    public void Detach()
    {
        _controller.Cancel(DragCancelReason.Unloaded);
        _activation?.Dispose();
        _activation = null;
    }

    /// <summary>Called by the controller for every item it collects: the V2 pen/touch arbitration.</summary>
    public static void PrepareItem(RadioButton item) => item.ManipulationMode = SegmentDragRules.ItemManipulation;

    public void Cancel(DragCancelReason reason) => _controller.Cancel(reason);

    /// <summary>A Checked/Unchecked the adapter did not write: an external selection wins over the preview.</summary>
    public void OnSelectionChanged()
    {
        if (!_committing)
        {
            _controller.Cancel(DragCancelReason.ExternalSelection);
        }
    }

    // ---- input ---------------------------------------------------------------------------------------------------------

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_controller.IsActive)
        {
            return; // one pointer at a time
        }

        var index = ItemOf(e.OriginalSource);
        _pointer = e.Pointer;
        _origin = index >= 0 ? _presenter.Items[index] : null;
        _controller.Press(Sample(e), index);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_controller.IsActive)
        {
            _controller.Move(Sample(e));
        }
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_controller.IsActive)
        {
            _controller.Release(Sample(e));
        }
    }

    private void OnCanceled(object sender, PointerRoutedEventArgs e) => _controller.Canceled(e.Pointer.PointerId);

    private void OnCaptureLost(object sender, PointerRoutedEventArgs e) =>
        _controller.CaptureLost(e.Pointer.PointerId, panelOwned: ReferenceEquals(e.OriginalSource, _panel));

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var key = e.Key;
        if (key == Windows.System.VirtualKey.Escape)
        {
            if (_controller.Key(isEscape: true))
            {
                e.Handled = true;
            }
        }
        else if (key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Up
                 or Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Tab)
        {
            _controller.Key(isEscape: false); // cancel first; the key keeps its normal meaning
        }
    }

    private void OnWindowActivation(bool active) => _controller.WindowActivation(active);

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

    // ---- ISegmentDragPort ----------------------------------------------------------------------------------------------

    public DragGeometry? Snapshot()
    {
        var items = _presenter.Items;
        if (items.Count < 2)
        {
            return null;
        }

        var rects = items.Select(_presenter.Measure).ToList();
        // Horizontal tracks only (structural: the vertical sidebar can never drag, even if mis-opted).
        if (rects.Any(r => Math.Abs(r.Y - rects[0].Y) > 0.5 || Math.Abs(r.Height - rects[0].Height) > 0.5))
        {
            return null;
        }

        var confirmed = -1;
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].IsChecked == true)
            {
                confirmed = index;
                break;
            }
        }

        return confirmed < 0
            ? null
            : new DragGeometry(rects, items.Select(item => item.IsEnabled && item.Visibility == Visibility.Visible).ToList(), confirmed, IsGroupEnabled());
    }

    private bool IsGroupEnabled()
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

    public DragThreshold Threshold(DragPointerDevice device)
    {
        var scale = _host.XamlRoot?.RasterizationScale ?? 1;
        var dpi = (uint)Math.Round(96 * (double.IsFinite(scale) && scale > 0 ? scale : 1));
        return SegmentDragRules.Threshold(device, GetSystemMetricsForDpi(SmCxDrag, dpi), GetSystemMetricsForDpi(SmCyDrag, dpi), scale);
    }

    public void ReleaseOriginCapture(uint pointerId)
    {
        if (_origin is not null && _pointer is not null)
        {
            _origin.ReleasePointerCapture(_pointer);
        }
    }

    public bool CapturePanel(uint pointerId) => _pointer is not null && _panel.CapturePointer(_pointer);

    public void ReleasePanelCapture(uint pointerId)
    {
        if (_pointer is not null)
        {
            _panel.ReleasePointerCapture(_pointer);
        }
    }

    public void BeginPreview()
    {
        _predicted = -1;
        _presenter.BeginPreview();
    }

    public void Present(IndicatorRect rect, int predicted)
    {
        _presenter.PresentPreview(rect);
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

        _presenter.EndPreview(outcome == DragOutcome.Cancelled ? IndicatorCause.DragCancel : IndicatorCause.DragEnd, flushNow: outcome != DragOutcome.Committed);
    }

    public void Commit(int index)
    {
        var items = _presenter.Items;
        if (index < 0 || index >= items.Count)
        {
            return;
        }

        var target = items[index];
        var focusInGroup = _host.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && IsInside(focused, _panel);
        _committing = true;
        try
        {
            target.IsChecked = true;
        }
        finally
        {
            _committing = false;
        }

        if (focusInGroup && target.IsEnabled)
        {
            target.Focus(FocusState.Pointer);
        }
    }

    /// <summary>The predicted label lit (Checked state) and every other item Unchecked; -1 = each item's real state.</summary>
    private void ShowLabels(int predicted)
    {
        var items = _presenter.Items;
        for (var index = 0; index < items.Count; index++)
        {
            var lit = predicted < 0 ? items[index].IsChecked == true : index == predicted;
            VisualStateManager.GoToState(items[index], lit ? "Checked" : "Unchecked", false);
        }
    }

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
