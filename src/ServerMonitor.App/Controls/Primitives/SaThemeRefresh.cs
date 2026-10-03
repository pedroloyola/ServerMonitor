using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.5 fix round 2 (Beacon C1 M3): a live theme switch (Escuro → Claro with the page open) left the glass cards of the
/// open page painted with the previous theme's acrylic until the page was recreated. The <c>SaGlassSurfaceBrush</c> is
/// an <see cref="AcrylicBrush"/> per theme dictionary, but the acrylic already applied through the style is not swapped
/// on a live RequestedTheme change. Re-applying the style of the affected surfaces (only <see cref="Border"/>s whose
/// background is an acrylic brush; never a Control, whose template a style swap would rebuild) makes the
/// ThemeResource resolve again for the new theme. Attached once per page; idempotent.
/// </summary>
public static class SaThemeRefresh
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SaThemeRefresh), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(FrameworkElement element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(FrameworkElement element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.ActualThemeChanged -= OnActualThemeChanged;
        if (e.NewValue is true)
        {
            element.ActualThemeChanged += OnActualThemeChanged;
        }
    }

    private static void OnActualThemeChanged(FrameworkElement sender, object args) =>
        sender.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ReapplyAcrylicSurfaces(sender));

    /// <summary>Re-applies the style of every acrylic-backed Border under <paramref name="root"/>; returns how many.</summary>
    public static int ReapplyAcrylicSurfaces(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var count = 0;
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is Border { Background: AcrylicBrush, Style: { } style } border)
            {
                border.Style = null;
                border.Style = style;
                count++;
            }

            var children = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < children; index++)
            {
                pending.Push(VisualTreeHelper.GetChild(current, index));
            }
        }

        return count;
    }
}
