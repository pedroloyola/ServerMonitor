using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using ServerMonitor.App.Services.Motion;
using Windows.Foundation;
using Windows.UI;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.11 H01/H03 (Prism §2.3, Cortex §2 option a). ONE selection indicator per segmented track (and the sidebar, F07)
/// that slides from the previous item to the new one instead of one pill vanishing and another appearing.
/// <para>
/// Set on an EMPTY, hit-test-invisible host element placed BEHIND the items in the same cell (first child of the Grid
/// that holds the RadioButtons, or of a Grid wrapping their StackPanel). The items are the RadioButtons among the host's
/// siblings and among the children of a sibling Panel. The item templates no longer paint their own selection fill (they
/// keep text, hover and pressed); this indicator paints it with the same measured brush and the item's corner radius,
/// plus - for the rect family - the 1 px top glass highlight, so the resting look matches UI.10.
/// </para>
/// <para>
/// Composition, off the UI thread and without layout: a <see cref="ShapeVisual"/> child of the host with a rounded
/// rectangle geometry whose Offset and Size animate (the radius is a fixed geometry property, so a width change never
/// distorts it). Every new target restarts the animation from the PRESENTED value (<c>this.StartingValue</c>), so rapid
/// input converges on the last selection, never queues, and there is never a second indicator. The logical selection
/// (IsChecked, the view model, UI Automation SelectionItem) never waits for it; the host is Raw for UI Automation.
/// Decisions come from the pure <see cref="SelectionIndicatorPlanner"/>; nothing runs on a timer, and every animation is
/// finite, so the compositor is idle at rest. Unloaded only unsubscribes: the Composition objects stay with the host, so a
/// theme remount (UI.5 SaThemeRefresh) does not cut a running slide (F04).
/// </para>
/// </summary>
public static class SaSlidingSelection
{
    /// <summary>The selection fill (a SolidColorBrush token, normally a ThemeResource). Setting it attaches the indicator.</summary>
    public static readonly DependencyProperty IndicatorBrushProperty = DependencyProperty.RegisterAttached(
        "IndicatorBrush", typeof(Brush), typeof(SaSlidingSelection), new PropertyMetadata(null, OnBrushChanged));

    /// <summary>Optional 1 px top glass highlight that moves with the indicator (rect family).</summary>
    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.RegisterAttached(
        "HighlightBrush", typeof(Brush), typeof(SaSlidingSelection), new PropertyMetadata(null, OnBrushChanged));

    /// <summary>
    /// UI.11 F20 (Prism rev.4 D-B1, DD-UI11-1): a 1 px inner hairline drawn ONLY in the Light theme (not in Dark, not in High
    /// Contrast). The rect family's Light fill (#FFF@.50) is Δ1 against its track - the Figma pill is separated by a drop
    /// shadow the app does not reproduce - so without it the sliding pill would be invisible in Light. The rect hosts pass
    /// SaDialogButtonBorderBrush (the dialog buttons' Light hairline); other families leave it unset.
    /// </summary>
    public static readonly DependencyProperty LightOutlineBrushProperty = DependencyProperty.RegisterAttached(
        "LightOutlineBrush", typeof(Brush), typeof(SaSlidingSelection), new PropertyMetadata(null, OnBrushChanged));

    public static Brush? GetLightOutlineBrush(FrameworkElement host) => (Brush?)host.GetValue(LightOutlineBrushProperty);

    public static void SetLightOutlineBrush(FrameworkElement host, Brush? value) => host.SetValue(LightOutlineBrushProperty, value);

    private static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached(
        "Controller", typeof(object), typeof(SaSlidingSelection), new PropertyMetadata(null));

    public static Brush? GetIndicatorBrush(FrameworkElement host) => (Brush?)host.GetValue(IndicatorBrushProperty);

    public static void SetIndicatorBrush(FrameworkElement host, Brush? value) => host.SetValue(IndicatorBrushProperty, value);

    public static Brush? GetHighlightBrush(FrameworkElement host) => (Brush?)host.GetValue(HighlightBrushProperty);

    public static void SetHighlightBrush(FrameworkElement host, Brush? value) => host.SetValue(HighlightBrushProperty, value);

    /// <summary>The opacity of a disabled item (the item templates' Disabled state); the indicator dims with it.</summary>
    internal const double DisabledOpacity = 0.58;

    /// <summary>QA/test seam: the controller of a host, or null when none is attached.</summary>
    internal static SlidingSelectionController? ControllerOf(FrameworkElement host) => host.GetValue(ControllerProperty) as SlidingSelectionController;

    private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement host)
        {
            return;
        }

        if (ControllerOf(host) is { } existing)
        {
            existing.UpdateBrushes();
            return;
        }

        if (GetIndicatorBrush(host) is not null)
        {
            host.SetValue(ControllerProperty, new SlidingSelectionController(host));
        }
    }

    internal static Color Effective(Brush? brush) => brush is SolidColorBrush solid
        ? Color.FromArgb((byte)Math.Round(solid.Color.A * Math.Clamp(solid.Opacity, 0, 1)), solid.Color.R, solid.Color.G, solid.Color.B)
        : Color.FromArgb(0, 0, 0, 0);
}

/// <summary>The per-host state of <see cref="SaSlidingSelection"/>. Lives as long as the host element.</summary>
internal sealed class SlidingSelectionController
{
    private readonly FrameworkElement _host;
    private readonly SelectionIndicatorPlanner _planner = new();
    private readonly List<RadioButton> _items = [];
    private bool _loaded;
    private bool _flushQueued;
    private XamlRoot? _xamlRoot;
    private double _rasterizationScale;

    private Compositor? _compositor;
    private ShapeVisual? _visual;
    private CompositionContainerShape? _container;
    private CompositionRoundedRectangleGeometry? _fillGeometry;
    private CompositionColorBrush? _fillBrush;
    private CompositionRoundedRectangleGeometry? _highlightGeometry;
    private CompositionSpriteShape? _highlightShape;
    private CompositionLinearGradientBrush? _highlightBrush;
    private CompositionSpriteShape? _outlineShape;
    private CompositionColorBrush? _outlineBrush;
    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();
    private float _radius = -1;

    public SlidingSelectionController(FrameworkElement host)
    {
        _host = host;
        _host.Loaded += OnLoaded;
        _host.Unloaded += OnUnloaded;
        if (_host.IsLoaded)
        {
            OnLoaded(_host, null);
        }
    }

    /// <summary>QA/test seam: what the planner last presented (null while hidden).</summary>
    public IndicatorRect? PresentedTarget => _planner.PresentedTarget;

    /// <summary>QA/test seam: how many indicator visuals this track owns (the H03 invariant: always at most one).</summary>
    public int IndicatorCount => _visual is null ? 0 : 1;

    public void UpdateBrushes()
    {
        if (_fillBrush is null)
        {
            return;
        }

        _fillBrush.Color = SaSlidingSelection.Effective(SaSlidingSelection.GetIndicatorBrush(_host));
        UpdateOutline();
        UpdateHighlightStops();
    }

    private void OnLoaded(object sender, RoutedEventArgs? e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        CollectItems();
        _host.SizeChanged += OnLayoutChanged;
        _xamlRoot = _host.XamlRoot;
        if (_xamlRoot is not null)
        {
            _rasterizationScale = _xamlRoot.RasterizationScale;
            _xamlRoot.Changed += OnXamlRootChanged;
        }

        MotionPolicy.Source.Changed += OnReducedMotionChanged;
        _host.ActualThemeChanged += OnThemeChanged;
        EnsureVisual();
        UpdateBrushes();
        _planner.Request(_planner.PresentedTarget is null ? IndicatorCause.FirstLayout : IndicatorCause.Remount);
        QueueFlush();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Unsubscribe only. The Composition objects stay attached to the host, so a theme remount (out of the tree and
        // straight back in) keeps a running slide; they go with the host when it is collected.
        _loaded = false;
        _host.SizeChanged -= OnLayoutChanged;
        if (_xamlRoot is not null)
        {
            _xamlRoot.Changed -= OnXamlRootChanged;
            _xamlRoot = null;
        }

        MotionPolicy.Source.Changed -= OnReducedMotionChanged;
        _host.ActualThemeChanged -= OnThemeChanged;
        ReleaseItems();
    }

    private void CollectItems()
    {
        ReleaseItems();
        if (VisualTreeHelper.GetParent(_host) is Panel parent)
        {
            foreach (var child in parent.Children)
            {
                if (ReferenceEquals(child, _host))
                {
                    continue;
                }

                if (child is RadioButton item)
                {
                    _items.Add(item);
                }
                else if (child is Panel panel)
                {
                    _items.AddRange(panel.Children.OfType<RadioButton>());
                }
            }
        }

        foreach (var item in _items)
        {
            item.Checked += OnSelectionChanged;
            item.Unchecked += OnSelectionChanged;
            item.SizeChanged += OnLayoutChanged;
            item.IsEnabledChanged += OnItemEnabledChanged;
        }
    }

    private void ReleaseItems()
    {
        foreach (var item in _items)
        {
            item.Checked -= OnSelectionChanged;
            item.Unchecked -= OnSelectionChanged;
            item.SizeChanged -= OnLayoutChanged;
            item.IsEnabledChanged -= OnItemEnabledChanged;
        }

        _items.Clear();
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        _planner.Request(IndicatorCause.Selection);
        QueueFlush();
    }

    private void OnLayoutChanged(object sender, SizeChangedEventArgs e)
    {
        _planner.Request(IndicatorCause.Resize);
        QueueFlush();
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.RasterizationScale == _rasterizationScale)
        {
            return;
        }

        _rasterizationScale = sender.RasterizationScale;
        _planner.Request(IndicatorCause.DpiChange);
        QueueFlush();
    }

    private void OnReducedMotionChanged(object? sender, EventArgs e)
    {
        _planner.Request(IndicatorCause.ReducedMotionChanged);
        QueueFlush();
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => UpdateBrushes();

    private void OnItemEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateOpacity();

    /// <summary>
    /// The target is measured AFTER the new visual state's layout (an auto-width item may re-measure on selection): at Low
    /// priority, once per burst of requests. No timer.
    /// </summary>
    private void QueueFlush()
    {
        if (_flushQueued || !_loaded)
        {
            return;
        }

        _flushQueued = _host.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Flush);
    }

    private void Flush()
    {
        _flushQueued = false;
        if (!_loaded || _visual is null)
        {
            return; // stays pending; the next Loaded re-plans
        }

        var rects = new List<IndicatorRect>(_items.Count);
        var selected = -1;
        for (var index = 0; index < _items.Count; index++)
        {
            rects.Add(RectOf(_items[index]));
            if (selected < 0 && _items[index].IsChecked == true)
            {
                selected = index;
            }
        }

        var plan = _planner.Flush(rects, selected, MotionPolicy.IsReduced);
        Apply(plan, selected >= 0 ? _items[selected] : null);
    }

    private IndicatorRect RectOf(RadioButton item)
    {
        if (item.Visibility != Visibility.Visible || item.ActualWidth <= 0 || item.ActualHeight <= 0)
        {
            return default;
        }

        var origin = item.TransformToVisual(_host).TransformPoint(new Point(0, 0));
        return new IndicatorRect(origin.X, origin.Y, item.ActualWidth, item.ActualHeight);
    }

    private void EnsureVisual()
    {
        if (_visual is not null)
        {
            return;
        }

        _compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
        _visual = _compositor.CreateShapeVisual();
        _visual.RelativeSizeAdjustment = Vector2.One;
        _visual.IsVisible = false;
        _container = _compositor.CreateContainerShape();
        _fillGeometry = _compositor.CreateRoundedRectangleGeometry();
        _fillBrush = _compositor.CreateColorBrush();
        var fill = _compositor.CreateSpriteShape(_fillGeometry);
        fill.FillBrush = _fillBrush;
        _container.Shapes.Add(fill);

        _highlightGeometry = _compositor.CreateRoundedRectangleGeometry();
        _highlightGeometry.Offset = new Vector2(0.5f, 0.5f);
        // F20: the Light hairline shares the inset geometry (a 1 px stroke centred 0.5 px inside = an inner border).
        _outlineBrush = _compositor.CreateColorBrush();
        _outlineShape = _compositor.CreateSpriteShape(_highlightGeometry);
        _outlineShape.StrokeBrush = _outlineBrush;
        _outlineShape.StrokeThickness = 0;
        _container.Shapes.Add(_outlineShape);
        _highlightBrush = _compositor.CreateLinearGradientBrush();
        _highlightBrush.MappingMode = CompositionMappingMode.Absolute;
        _highlightShape = _compositor.CreateSpriteShape(_highlightGeometry);
        _highlightShape.StrokeThickness = 1;
        _highlightShape.StrokeBrush = _highlightBrush;
        _container.Shapes.Add(_highlightShape);

        _visual.Shapes.Add(_container);
        ElementCompositionPreview.SetElementChildVisual(_host, _visual);
    }

    private void Apply(IndicatorPlan plan, RadioButton? selected)
    {
        if (_visual is null || _container is null || _fillGeometry is null || _highlightGeometry is null || _compositor is null)
        {
            return;
        }

        switch (plan.Action)
        {
            case IndicatorAction.Hide:
                StopAll();
                _visual.IsVisible = false;
                return;
            case IndicatorAction.NoOp:
                UpdateOpacity();
                return;
        }

        if (selected is not null)
        {
            UpdateRadius((float)selected.CornerRadius.TopLeft);
        }

        UpdateOpacity();
        var offset = new Vector2((float)plan.Target.X, (float)plan.Target.Y);
        var size = new Vector2((float)plan.Target.Width, (float)plan.Target.Height);
        var highlightSize = new Vector2(Math.Max(0, size.X - 1), Math.Max(0, size.Y - 1));
        if (plan.Action == IndicatorAction.Snap)
        {
            StopAll();
            _container.Offset = offset;
            _fillGeometry.Size = size;
            _highlightGeometry.Size = highlightSize;
        }
        else
        {
            var resources = Application.Current.Resources;
            var duration = MotionTokens.GetTime(resources, MotionTokens.SelectDuration);
            var spline = MotionTokens.GetKeySpline(resources, MotionTokens.PointToPointKeySpline);
            var easing = _compositor.CreateCubicBezierEasingFunction(
                new Vector2((float)spline.X1, (float)spline.Y1), new Vector2((float)spline.X2, (float)spline.Y2));
            _container.StartAnimation("Offset", Animation(offset, duration, easing));
            _fillGeometry.StartAnimation("Size", Animation(size, duration, easing));
            _highlightGeometry.StartAnimation("Size", Animation(highlightSize, duration, easing));
        }

        _visual.IsVisible = true;
    }

    /// <summary>One finite key-frame animation from the presented value (retarget, never restart from a fixed origin).</summary>
    private Vector2KeyFrameAnimation Animation(Vector2 to, TimeSpan duration, CompositionEasingFunction easing)
    {
        var animation = _compositor!.CreateVector2KeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0f, "this.StartingValue");
        animation.InsertKeyFrame(1f, to, easing);
        animation.Duration = duration;
        animation.IterationBehavior = AnimationIterationBehavior.Count;
        animation.IterationCount = 1;
        return animation;
    }

    private void StopAll()
    {
        _container?.StopAnimation("Offset");
        _fillGeometry?.StopAnimation("Size");
        _highlightGeometry?.StopAnimation("Size");
    }

    private void UpdateRadius(float radius)
    {
        if (radius == _radius || _fillGeometry is null || _highlightGeometry is null)
        {
            return;
        }

        _radius = radius;
        _fillGeometry.CornerRadius = new Vector2(radius, radius);
        var inner = Math.Max(0, radius - 0.5f);
        _highlightGeometry.CornerRadius = new Vector2(inner, inner);
        UpdateHighlightStops();
    }

    private void UpdateOutline()
    {
        if (_outlineShape is null || _outlineBrush is null)
        {
            return;
        }

        var color = SelectionIndicatorRules.DrawsLightOutline(
                SaSlidingSelection.GetLightOutlineBrush(_host) is not null, _host.ActualTheme == ElementTheme.Light, Accessibility.HighContrast)
            ? SaSlidingSelection.Effective(SaSlidingSelection.GetLightOutlineBrush(_host))
            : Color.FromArgb(0, 0, 0, 0);
        _outlineBrush.Color = color;
        _outlineShape.StrokeThickness = color.A == 0 ? 0 : 1;
    }

    private void UpdateHighlightStops()
    {
        if (_highlightBrush is null || _compositor is null || _highlightShape is null)
        {
            return;
        }

        var color = SaSlidingSelection.Effective(SaSlidingSelection.GetHighlightBrush(_host));
        _highlightShape.StrokeThickness = color.A == 0 ? 0 : 1;
        var radius = Math.Max(1f, _radius);
        _highlightBrush.StartPoint = new Vector2(0, 0.5f);
        _highlightBrush.EndPoint = new Vector2(0, 0.5f + radius);
        _highlightBrush.ColorStops.Clear();
        foreach (var (offset, alpha) in IndicatorHighlight.Falloff)
        {
            _highlightBrush.ColorStops.Add(_compositor.CreateColorGradientStop(
                offset, Color.FromArgb((byte)Math.Round(color.A * alpha), color.R, color.G, color.B)));
        }
    }

    private void UpdateOpacity()
    {
        var selected = _items.FirstOrDefault(item => item.IsChecked == true);
        _host.Opacity = selected is null || selected.IsEnabled ? 1 : SaSlidingSelection.DisabledOpacity;
    }
}
