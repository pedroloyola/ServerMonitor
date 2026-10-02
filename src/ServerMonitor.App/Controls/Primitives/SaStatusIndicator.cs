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
[TemplateVisualState(Name = "LabelVisible", GroupName = "LabelStates")]
[TemplateVisualState(Name = "LabelHidden", GroupName = "LabelStates")]
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
        new PropertyMetadata(string.Empty, (d, e) => ((SaStatusIndicator)d).ApplyAutomationName(e.NewValue as string ?? string.Empty)));

    /// <summary>
    /// UI.3: false only for the dot-only presentation (SaStatusDotOnlyStyle), where the row next to the dot already
    /// states the status in text. The Label stays required and stays the automation name; only its rendering hides.
    /// </summary>
    public static readonly DependencyProperty IsLabelVisibleProperty = DependencyProperty.Register(
        nameof(IsLabelVisible),
        typeof(bool),
        typeof(SaStatusIndicator),
        new PropertyMetadata(true, (d, _) => ((SaStatusIndicator)d).UpdateLabelState(useTransitions: false)));

    internal const string LabelVisibleState = "LabelVisible";
    internal const string LabelHiddenState = "LabelHidden";

    // The name this control last applied. An explicit AutomationProperties.Name set by a consumer is never
    // overwritten (Cortex F-2): only an empty name or the one this control set itself is replaced.
    private string? _appliedAutomationName;
    private ElementTheme? _stateTheme;

    public SaStatusIndicator()
    {
        DefaultStyleKey = typeof(SaStatusIndicator);
        // UI.4 QA r2: a {ThemeResource} in an ACTIVE visual state's setter is not re-resolved on a runtime theme change
        // (WinUI), so the dot kept the previous theme's colour until the row was recycled. Re-enter the state when the
        // effective theme differs from the one it was applied under - also on Loaded, because a cached page (the Visão
        // geral) is out of the tree while the theme is changed in Definições.
        ActualThemeChanged += (_, _) => RefreshStatusStateForTheme();
        Loaded += (_, _) => RefreshStatusStateForTheme();
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

    public bool IsLabelVisible
    {
        get => (bool)GetValue(IsLabelVisibleProperty);
        set => SetValue(IsLabelVisibleProperty, value);
    }

    /// <summary>The visual state that represents <paramref name="status"/>; an undefined value is Unknown.</summary>
    public static string StateName(SaStatusKind status) =>
        Enum.IsDefined(status) ? status.ToString() : nameof(SaStatusKind.Unknown);

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateStatusState(useTransitions: false);
        _stateTheme = ActualTheme;
        UpdateLabelState(useTransitions: false);
    }

    private void ApplyAutomationName(string label)
    {
        var current = AutomationProperties.GetName(this);
        if (string.IsNullOrEmpty(current) || current == _appliedAutomationName)
        {
            AutomationProperties.SetName(this, label);
            _appliedAutomationName = label;
        }
    }

    private void RefreshStatusStateForTheme()
    {
        if (_stateTheme is null || _stateTheme == ActualTheme)
        {
            return;
        }

        // GoToState to the state already active is a no-op; pass through another one to re-apply the setters.
        var current = StateName(Status);
        VisualStateManager.GoToState(this, current == nameof(SaStatusKind.Unknown) ? nameof(SaStatusKind.Healthy) : nameof(SaStatusKind.Unknown), false);
        VisualStateManager.GoToState(this, current, false);
        _stateTheme = ActualTheme;
    }

    private void UpdateStatusState(bool useTransitions) =>
        VisualStateManager.GoToState(this, StateName(Status), useTransitions);

    private void UpdateLabelState(bool useTransitions) =>
        VisualStateManager.GoToState(this, IsLabelVisible ? LabelVisibleState : LabelHiddenState, useTransitions);
}
