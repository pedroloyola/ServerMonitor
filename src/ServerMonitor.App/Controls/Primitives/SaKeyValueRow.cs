using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.2 key/value (Figma 112:1906 stacked: label 12 muted over value 17 Medium, gap 8; 112:1919 inline: label 13
/// muted in a 190 column, value 13 Medium, gap 10). Text only; the automation name is "key: value".
/// </summary>
[TemplateVisualState(Name = "Stacked", GroupName = "OrientationStates")]
[TemplateVisualState(Name = "Inline", GroupName = "OrientationStates")]
public sealed class SaKeyValueRow : Control
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key), typeof(string), typeof(SaKeyValueRow), new PropertyMetadata(string.Empty, (d, _) => ((SaKeyValueRow)d).Update()));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(SaKeyValueRow), new PropertyMetadata(string.Empty, (d, _) => ((SaKeyValueRow)d).Update()));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(SaKeyValueOrientation), typeof(SaKeyValueRow),
        new PropertyMetadata(SaKeyValueOrientation.Stacked, (d, _) => ((SaKeyValueRow)d).Update()));

    public SaKeyValueRow()
    {
        DefaultStyleKey = typeof(SaKeyValueRow);
        IsTabStop = false;
    }

    public string Key
    {
        get => (string)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public SaKeyValueOrientation Orientation
    {
        get => (SaKeyValueOrientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Update();
    }

    private void Update()
    {
        AutomationProperties.SetName(this, $"{Key}: {Value}");
        VisualStateManager.GoToState(this, Orientation == SaKeyValueOrientation.Inline ? "Inline" : "Stacked", useTransitions: false);
    }
}
