using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace ServerMonitor.App.Qa.Gallery;

/// <summary>
/// QA-ONLY. The Figma page-05 wallpaper behind the app window (vector 112:931 Dark / 112:1142 Light: a gradient
/// #090909 -> #1F1F1F / #F5F5F5 -> #DBDBDB), so translucent and glass samples are judged on the background they were
/// designed on. It is a reference backdrop, not a token: the colours are numeric here and never reach production.
/// Follows the element's ActualTheme. R1 (Prism MF-4): while the HC resource simulation is on, the wallpaper is NOT
/// painted, so the simulated canvas (the HC Window colour on SimulationHost) shows through - never a dark Figma
/// backdrop under WindowText.
/// </summary>
public static class QaWallpaper
{
    /// <summary>Set by the gallery window while hc-sim is on (pages are recreated when it changes).</summary>
    internal static bool Suppressed { get; set; }

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(QaWallpaper), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true)
        {
            return;
        }

        element.ActualThemeChanged += (sender, _) => Paint(sender);
        element.Loaded += (sender, _) => Paint((FrameworkElement)sender);
        Paint(element);
    }

    private static void Paint(FrameworkElement element)
    {
        if (Suppressed)
        {
            SetBackground(element, null);
            return;
        }

        var dark = element.ActualTheme != ElementTheme.Light;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = dark ? Color.FromArgb(255, 9, 9, 9) : Color.FromArgb(255, 245, 245, 245) });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = dark ? Color.FromArgb(255, 31, 31, 31) : Color.FromArgb(255, 219, 219, 219) });
        SetBackground(element, brush);
    }

    private static void SetBackground(FrameworkElement element, Brush? brush)
    {
        switch (element)
        {
            case Panel panel:
                panel.Background = brush;
                break;
            case Border border:
                border.Background = brush;
                break;
            case Control control:
                control.Background = brush;
                break;
        }
    }
}
