using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
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

    public SaIcon()
    {
        DefaultStyleKey = typeof(SaIcon);
    }

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

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _path = GetTemplateChild(PathPartName) as Path;
        UpdateGeometry();
    }

    private void UpdateGeometry()
    {
        if (_path is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Data))
        {
            _path.Data = null;
            return;
        }

        // A fresh geometry per instance: the Transform is per icon, so a shared Geometry must never be mutated.
        var geometry = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), Data);
        var scale = ScaleFor(Size);
        geometry.Transform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
        _path.Data = geometry;
    }
}
