using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.7 final c1 (Prism F-3): the brush of the 2 px focus ring that the Sa text field and password box templates draw OVER
/// the border. Set by the field styles: the regular style uses SaFocusRingBrush, the error style SaErrorBrush - so the
/// field the user lands on after a failed attempt keeps its danger edge while focused (the caret shows the focus).
/// </summary>
public static class SaFieldChrome
{
    public static readonly DependencyProperty FocusRingBrushProperty = DependencyProperty.RegisterAttached(
        "FocusRingBrush", typeof(Brush), typeof(SaFieldChrome), new PropertyMetadata(null));

    public static Brush? GetFocusRingBrush(DependencyObject element) => (Brush?)element.GetValue(FocusRingBrushProperty);

    public static void SetFocusRingBrush(DependencyObject element, Brush? value) => element.SetValue(FocusRingBrushProperty, value);
}
