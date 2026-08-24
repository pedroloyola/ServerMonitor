using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace ServerMonitor.App.Controls;

[ContentProperty(Name = nameof(CardContent))]
public sealed partial class GlassCard : UserControl
{
    public static readonly DependencyProperty CardContentProperty = DependencyProperty.Register(
        nameof(CardContent),
        typeof(object),
        typeof(GlassCard),
        new PropertyMetadata(null));

    public GlassCard()
    {
        InitializeComponent();
    }

    public object? CardContent
    {
        get => GetValue(CardContentProperty);
        set => SetValue(CardContentProperty, value);
    }
}
