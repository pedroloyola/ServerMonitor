using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.2 R1 (Beacon F-3): the Windows keyboard pattern for a group of RadioButton-based items (segmented control, sidebar
/// nav), set on the panel that holds them. The group is ONE Tab stop (TabFocusNavigation = Once); entering it lands on
/// the checked item; the arrow keys move focus to the previous/next enabled item (no wrap). Segmented: selection follows
/// keyboard focus. Navigation: arrows move focus only - Space/Enter activate (the RadioButton's own behaviour).
/// The arrows are handled HERE, on the panel: XYFocusKeyboardNavigation never sees them inside a ScrollViewer, which
/// marks arrow keys handled for scrolling (measured in the gallery, R1 keyboard probe).
/// </summary>
public static class SaGroupNavigation
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(SaGroupNavigationMode), typeof(SaGroupNavigation), new PropertyMetadata(SaGroupNavigationMode.None, OnModeChanged));

    public static SaGroupNavigationMode GetMode(Panel panel) => (SaGroupNavigationMode)panel.GetValue(ModeProperty);

    public static void SetMode(Panel panel, SaGroupNavigationMode value) => panel.SetValue(ModeProperty, value);

    /// <summary>Pure rule (unit-tested): does keyboard focus arriving on an item select it?</summary>
    internal static bool SelectsOnFocus(SaGroupNavigationMode mode, FocusState focusState) =>
        mode == SaGroupNavigationMode.SelectionFollowsFocus && focusState == FocusState.Keyboard;

    /// <summary>Pure rule (unit-tested): the item index an arrow key moves to, or null when it does not move (ends, other keys).</summary>
    internal static int? ArrowTarget(Windows.System.VirtualKey key, int current, int count)
    {
        var step = key switch
        {
            Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Up => -1,
            Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Down => 1,
            _ => 0
        };
        var target = current + step;
        return step != 0 && current >= 0 && target >= 0 && target < count ? target : null;
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel panel)
        {
            return;
        }

        panel.GettingFocus -= OnGettingFocus;
        panel.GotFocus -= OnGotFocus;
        panel.RemoveHandler(UIElement.KeyDownEvent, KeyDownHandler);
        if ((SaGroupNavigationMode)e.NewValue == SaGroupNavigationMode.None)
        {
            panel.ClearValue(UIElement.TabFocusNavigationProperty);
            return;
        }

        panel.TabFocusNavigation = KeyboardNavigationMode.Once;
        panel.GettingFocus += OnGettingFocus;
        panel.GotFocus += OnGotFocus;
        // handledEventsToo: an item (or a template part) may mark the arrow handled before it bubbles to the panel.
        panel.AddHandler(UIElement.KeyDownEvent, KeyDownHandler, handledEventsToo: true);
    }

    /// <summary>Focus entering the group from outside (Tab / Shift+Tab) is redirected to the checked item.</summary>
    private static void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        var panel = (Panel)sender;
        if (args.InputDevice != FocusInputDeviceKind.Keyboard || IsInside(panel, args.OldFocusedElement as DependencyObject))
        {
            return;
        }

        var checkedItem = panel.Children.OfType<RadioButton>().FirstOrDefault(item => item.IsChecked == true && item.IsEnabled);
        if (checkedItem is not null && !ReferenceEquals(checkedItem, args.NewFocusedElement))
        {
            args.TrySetNewFocusedElement(checkedItem);
        }
    }

    private static readonly KeyEventHandler KeyDownHandler = OnKeyDown;

    private static void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is not RadioButton item)
        {
            return;
        }

        var items = ((Panel)sender).Children.OfType<RadioButton>().Where(i => i.IsEnabled && i.Visibility == Visibility.Visible).ToList();
        if (ArrowTarget(e.Key, items.IndexOf(item), items.Count) is { } target)
        {
            items[target].Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    private static void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is RadioButton item && SelectsOnFocus(GetMode((Panel)sender), item.FocusState))
        {
            item.IsChecked = true;
        }
    }

    private static bool IsInside(Panel panel, DependencyObject? element)
    {
        for (var current = element; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, panel))
            {
                return true;
            }
        }

        return false;
    }
}
