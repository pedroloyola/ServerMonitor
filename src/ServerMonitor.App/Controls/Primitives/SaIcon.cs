using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.2 icon primitive (Boss decision B-1). Draws one vendored Hugeicons path (24x24 design grid, stroke-only)
/// with <see cref="Control.Foreground"/> as the stroke, so it recolours with the theme and in High Contrast.
/// <para>
/// The geometry - not the element - is scaled (<c>Geometry.Transform</c> = Size/24), so the stroke stays exactly
/// <see cref="StrokeWidth"/> DIPs at every size, as the Figma icons do (1.5 from 14 to 48 px, Prism §3.2). A
/// Viewbox or RenderTransform would scale the stroke with the icon.
/// </para>
/// Decorative by default (Raw in UI Automation, not a tab stop): the control that hosts it carries the name.
/// </summary>
[TemplatePart(Name = PathPartName, Type = typeof(Path))]
public sealed class SaIcon : Control
{
    /// <summary>The design grid of the vendored icons.</summary>
    public const double DesignSize = 24;

    /// <summary>The constant stroke width of the set, in DIPs.</summary>
    public const double StrokeWidth = 1.5;

    private const string PathPartName = "PART_Path";

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(string), typeof(SaIcon), new PropertyMetadata(null, (d, _) => ((SaIcon)d).UpdateGeometry()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(SaIcon), new PropertyMetadata(20d, (d, _) => ((SaIcon)d).UpdateGeometry()));

    private Path? _path;
    private ContentPresenter? _hostPresenter;
    private long _hostToken;
    private bool _strokeFollowsHost;

    public SaIcon()
    {
        DefaultStyleKey = typeof(SaIcon);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>UI.12 B12B-01: what an ancestor of the icon is, walking up the visual tree.</summary>
    internal enum IconAncestor
    {
        Other,
        Presenter,
        Button,
        OtherControl,
    }

    /// <summary>
    /// UI.12 B12B-01 (Beacon, measured in real High Contrast): the hover / pressed / checked states of a button swap ITS
    /// ContentPresenter's Foreground (SaHoverTextBrush / SaSelectedTextBrush = HighlightText on the Highlight fill), but
    /// the icon never follows that swap (measured: the checked sidebar icon keeps its rest colour in Dark too), so in HC it
    /// stayed WindowText on Highlight (1.44-1.78:1). The icon of a button is the one whose nearest Control ancestor is a
    /// ButtonBase; its stroke follows the ContentPresenter met on the way there. Returns that presenter's index in
    /// <paramref name="chain"/> (nearest first), or -1 - another Control first (a list item, a user control) or no presenter.
    /// </summary>
    internal static int HostPresenterIndex(IReadOnlyList<IconAncestor> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var presenter = -1;
        for (var i = 0; i < chain.Count; i++)
        {
            switch (chain[i])
            {
                case IconAncestor.Presenter:
                    presenter = i;
                    break;
                case IconAncestor.Button:
                    return presenter;
                case IconAncestor.OtherControl:
                    return -1;
            }
        }

        return -1;
    }

    /// <summary>UI.12 B12B-01: only in High Contrast (Light / Dark keep exactly today's stroke), and only inside a button.</summary>
    internal static bool StrokeFollowsHost(bool highContrast, bool hasHostPresenter) => highContrast && hasHostPresenter;

    /// <summary>SVG path data on the 24x24 grid, normally a <c>SaIcon*Data</c> resource from Sa.Icons.xaml.</summary>
    public string? Data
    {
        get => (string?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>Rendered edge length in DIPs (measured uses: 11-48; tokens SaIconSize* 16/20/24).</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>The scale applied to the 24-unit geometry; the stroke is not scaled.</summary>
    public static double ScaleFor(double size) =>
        size > 0 && double.IsFinite(size)
            ? size / DesignSize
            : throw new ArgumentOutOfRangeException(nameof(size), size, "An icon size must be a positive, finite number of DIPs.");

    /// <summary>R2 (Cortex R-7): a size that can be drawn. Anything else (0, negative, NaN, infinity - e.g. from a binding)
    /// renders nothing instead of throwing from the property callback.</summary>
    public static bool IsRenderableSize(double size) => size > 0 && double.IsFinite(size);

    /// <summary>True at the design size, where no geometry transform may be applied (an identity transform renders nothing).</summary>
    public static bool IsIdentityScale(double scale) => Math.Abs(scale - 1) < 1e-9;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        DetachHost();
        _strokeFollowsHost = false; // a new PART_Path starts with the template's own stroke binding
        _path = GetTemplateChild(PathPartName) as Path;
        UpdateGeometry();
        if (IsLoaded)
        {
            UpdateStrokeSource();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SaHighContrastWatch.Changed -= OnHighContrastChanged;
        SaHighContrastWatch.Changed += OnHighContrastChanged;
        UpdateStrokeSource();
    }

    // A remount can raise the new Loaded before the stale Unloaded (UI.11C): still on the live tree = keep everything.
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (SaLiveTree.IsLive(this))
        {
            return;
        }

        SaHighContrastWatch.Changed -= OnHighContrastChanged;
        DetachHost();
    }

    // Raised off the UI thread (AccessibilitySettings): a theme switch to or from HC with the window open.
    private void OnHighContrastChanged() => DispatcherQueue?.TryEnqueue(UpdateStrokeSource);

    private void UpdateStrokeSource()
    {
        if (_path is null)
        {
            return;
        }

        var presenter = FindHostPresenter();
        if (StrokeFollowsHost(SaHighContrastWatch.IsOn, presenter is not null))
        {
            if (!ReferenceEquals(presenter, _hostPresenter))
            {
                DetachHost();
                _hostPresenter = presenter;
                _hostToken = presenter!.RegisterPropertyChangedCallback(ContentPresenter.ForegroundProperty, (_, _) => ApplyHostStroke());
            }

            _strokeFollowsHost = true;
            ApplyHostStroke();
        }
        else
        {
            DetachHost();
            if (_strokeFollowsHost)
            {
                // Back to the template's own rule (Stroke = this.Foreground), exactly what the TemplateBinding did.
                _strokeFollowsHost = false;
                _path.SetBinding(Shape.StrokeProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
            }
        }
    }

    private void ApplyHostStroke()
    {
        if (_path is not null && _hostPresenter is not null)
        {
            _path.Stroke = _hostPresenter.Foreground ?? Foreground;
        }
    }

    private void DetachHost()
    {
        if (_hostPresenter is not null)
        {
            _hostPresenter.UnregisterPropertyChangedCallback(ContentPresenter.ForegroundProperty, _hostToken);
            _hostPresenter = null;
        }
    }

    private ContentPresenter? FindHostPresenter()
    {
        var ancestors = new List<DependencyObject>();
        var chain = new List<IconAncestor>();
        for (var current = VisualTreeHelper.GetParent(this); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            var kind = current switch
            {
                ContentPresenter => IconAncestor.Presenter,
                ButtonBase => IconAncestor.Button,
                Control => IconAncestor.OtherControl,
                _ => IconAncestor.Other,
            };
            ancestors.Add(current);
            chain.Add(kind);
            if (kind is IconAncestor.Button or IconAncestor.OtherControl)
            {
                break;
            }
        }

        var index = HostPresenterIndex(chain);
        return index < 0 ? null : (ContentPresenter)ancestors[index];
    }

    private void UpdateGeometry()
    {
        if (_path is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Data) || !IsRenderableSize(Size))
        {
            _path.Data = null;
            return;
        }

        // A fresh geometry per instance: the Transform is per icon, so a shared Geometry must never be mutated.
        var geometry = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), Data);
        var scale = ScaleFor(Size);
        // EMPIRICAL (UI.2 --qa-tokens): an identity ScaleTransform (Size 24) makes WinUI render the path EMPTY, so the
        // native 24 size draws the geometry untransformed.
        geometry.Transform = IsIdentityScale(scale) ? null : new ScaleTransform { ScaleX = scale, ScaleY = scale };
        _path.Data = geometry;
    }
}

/// <summary>
/// UI.12 B12B-01: the system High Contrast flag and its changes, for <see cref="SaIcon"/>. Created on first use (never by
/// the pure rules the tests call); <see cref="Changed"/> is raised off the UI thread.
/// </summary>
internal static class SaHighContrastWatch
{
    private static readonly Lazy<AccessibilitySettings> Settings = new(() =>
    {
        var settings = new AccessibilitySettings();
        settings.HighContrastChanged += (_, _) => Changed?.Invoke();
        return settings;
    });

    public static event Action? Changed;

    public static bool IsOn => Settings.Value.HighContrast;
}
