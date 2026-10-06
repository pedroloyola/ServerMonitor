using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>Official brand vector, with its original 160-unit canvas preserved.</summary>
public sealed class SaBrandMark : Control
{
    public SaBrandMark() => DefaultStyleKey = typeof(SaBrandMark);
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(SaBrandMark), new PropertyMetadata(30d));
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
}
