using Microsoft.UI.Xaml;
namespace ServerMonitor.App.Controls.Primitives;
/// <summary>UI.6: page breakpoints use available CONTENT width, never the window including its sidebar.</summary>
public sealed class SaContentWidthTrigger : StateTriggerBase
{
    public static readonly DependencyProperty WidthProperty = DependencyProperty.Register(nameof(Width), typeof(double), typeof(SaContentWidthTrigger), new PropertyMetadata(0d, Changed));
    public static readonly DependencyProperty MinWidthProperty = DependencyProperty.Register(nameof(MinWidth), typeof(double), typeof(SaContentWidthTrigger), new PropertyMetadata(0d, Changed));
    public static readonly DependencyProperty MaxWidthProperty = DependencyProperty.Register(nameof(MaxWidth), typeof(double), typeof(SaContentWidthTrigger), new PropertyMetadata(double.MaxValue, Changed));
    public double Width { get => (double)GetValue(WidthProperty); set => SetValue(WidthProperty,value); }
    public double MinWidth { get => (double)GetValue(MinWidthProperty); set => SetValue(MinWidthProperty,value); }
    public double MaxWidth { get => (double)GetValue(MaxWidthProperty); set => SetValue(MaxWidthProperty,value); }
    public static bool Matches(double width, double min, double max) => width >= min && width < max;
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    { var trigger = (SaContentWidthTrigger)d; trigger.SetActive(Matches(trigger.Width,trigger.MinWidth,trigger.MaxWidth)); }
}
