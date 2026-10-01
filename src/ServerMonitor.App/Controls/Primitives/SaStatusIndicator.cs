using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.2 status primitive: a coloured dot plus a mandatory text label. Colour is never the only signal - the
/// label is always rendered and is the automation name. The dot brush comes from a visual state per
/// <see cref="SaStatusKind"/>, so a runtime theme change re-resolves it through ThemeResource (replacing the
/// converter-picks-a-brush pattern without touching production).
/// <para>
/// Templated control with no XAML of its own: its default style is the implicit
/// <c>primitives:SaStatusIndicator</c> style in Styles/Components/Sa.Primitives.xaml, merged into
/// Application.Resources. It takes primitive/enum dependency properties only and knows no view-model.
/// </para>
/// </summary>
[TemplateVisualState(Name = "Unknown", GroupName = "StatusStates")]
[TemplateVisualState(Name = "Healthy", GroupName = "StatusStates")]
[TemplateVisualState(Name = "Attention", GroupName = "StatusStates")]
[TemplateVisualState(Name = "Error", GroupName = "StatusStates")]
[TemplateVisualState(Name = "Offline", GroupName = "StatusStates")]
[TemplateVisualState(Name = "Stale", GroupName = "StatusStates")]
public sealed class SaStatusIndicator : Control
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status),
        typeof(SaStatusKind),
        typeof(SaStatusIndicator),
        new PropertyMetadata(SaStatusKind.Unknown, (d, _) => ((SaStatusIndicator)d).UpdateStatusState(useTransitions: true)));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label),
        typeof(string),
        typeof(SaStatusIndicator),
        new PropertyMetadata(string.Empty, (d, e) => AutomationProperties.SetName(d, e.NewValue as string ?? string.Empty)));

    public SaStatusIndicator()
    {
        DefaultStyleKey = typeof(SaStatusIndicator);
    }

    public SaStatusKind Status
    {
        get => (SaStatusKind)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>The visible text and the automation name. Required: colour is never the only signal.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The visual state that represents <paramref name="status"/>; an undefined value is Unknown.</summary>
    public static string StateName(SaStatusKind status) =>
        Enum.IsDefined(status) ? status.ToString() : nameof(SaStatusKind.Unknown);

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateStatusState(useTransitions: false);
    }

    private void UpdateStatusState(bool useTransitions) =>
        VisualStateManager.GoToState(this, StateName(Status), useTransitions);
}
