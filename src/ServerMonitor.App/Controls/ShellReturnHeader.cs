using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ServerMonitor.App.Controls.Primitives;

namespace ServerMonitor.App.Controls;

/// <summary>
/// M15.4a: "‹ Definições", drawn by the window above an unlisted page (a composition-supplied page reached by id, never
/// in the sidebar). An unlisted page cannot navigate by itself: this host control is its way back, to Settings, through
/// the navigation and its exit guard. It is the breadcrumb button of the app (the History detail's "‹" back), so the
/// keyboard, the focus ring and the Narrator work as there: the name says where it goes. Moving between sub-pages stays
/// inside the page itself. Created only when there is an unlisted page at all.
/// </summary>
internal static class ShellReturnHeader
{
    /// <summary>The visible label: where it goes.</summary>
    internal const string TextResourceKey = "ShellReturnToSettingsText";

    /// <summary>The Narrator's name.</summary>
    internal const string NameResourceKey = "ShellReturnToSettingsName";

    /// <summary>Below the window's drag strip (32).</summary>
    internal const double Top = 36;

    /// <summary>The pages' side padding: <c>SaPagePadding</c> (40) wide, <c>SaPagePaddingCompact</c> (24) below 700.</summary>
    internal const double Side = 40;

    internal const double CompactSide = 24;

    /// <summary>The content width below which the pages use their compact padding (their Narrow state).</summary>
    internal const double NarrowBelow = 700;

    /// <summary>M15.4a r4 (Prism P-5): aligned with the shown page's content, whose padding follows its width.</summary>
    internal static Thickness MarginFor(double contentWidth)
    {
        var side = contentWidth < NarrowBelow ? CompactSide : Side;
        return new Thickness(side, Top, side, 0);
    }

    internal const double Height = 22;

    /// <summary>The page moves down by this while the header is shown, so the header never covers it.</summary>
    internal const double FrameOffset = 30;

    internal static Button Create(string text, string name, Action activate)
    {
        var icon = new SaIcon
        {
            Data = (string)Application.Current.Resources["SaIconArrowRight01Data"],
            Size = 16,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = 180 },
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["SaBreadcrumbParentButtonStyle"],
            Height = Height,
            Margin = MarginFor(double.PositiveInfinity),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Content = content,
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => activate();
        return button;
    }
}
