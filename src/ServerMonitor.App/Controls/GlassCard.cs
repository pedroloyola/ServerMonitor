using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls;

public sealed class GlassCard : ContentControl
{
    public GlassCard()
    {
        DefaultStyleKey = typeof(GlassCard);
    }

    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer),
        typeof(object),
        typeof(GlassCard),
        new PropertyMetadata(null));

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }
}
