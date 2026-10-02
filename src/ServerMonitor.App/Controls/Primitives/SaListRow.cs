using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.2 navigable list row (Figma 112:1057 simple: icon 20 + name + trailing + chevron 18, h48; 112:1933 rich: icon
/// 22 + title/detail + chevron 16, h70). A Button, so it is focusable, invokable and announced as a button; the
/// automation name is the title. Hover / pressed / focus are DERIVED.
/// </summary>
[TemplateVisualState(Name = "Simple", GroupName = "VariantStates")]
[TemplateVisualState(Name = "Rich", GroupName = "VariantStates")]
public sealed class SaListRow : Button
{
    public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
        nameof(IconData), typeof(string), typeof(SaListRow), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SaListRow), new PropertyMetadata(string.Empty, (d, e) => AutomationProperties.SetName(d, e.NewValue as string ?? string.Empty)));

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(string), typeof(SaListRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TrailingProperty = DependencyProperty.Register(
        nameof(Trailing), typeof(object), typeof(SaListRow), new PropertyMetadata(null));

    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(SaListRowVariant), typeof(SaListRow),
        new PropertyMetadata(SaListRowVariant.Simple, (d, _) => ((SaListRow)d).UpdateVariant()));

    public SaListRow()
    {
        DefaultStyleKey = typeof(SaListRow);
    }

    /// <summary>UI.4 (Beacon r1 SHOULD-2): a Button that also says "x of N" when it is a row of a list.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new SaListRowAutomationPeer(this);

    /// <summary>Path data of the leading icon (a SaIcon*Data resource).</summary>
    public string? IconData
    {
        get => (string?)GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Second line of the Rich variant.</summary>
    public string Detail
    {
        get => (string)GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>Content before the chevron (e.g. a status indicator) in the Simple variant.</summary>
    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    public SaListRowVariant Variant
    {
        get => (SaListRowVariant)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateVariant();
    }

    private void UpdateVariant() =>
        VisualStateManager.GoToState(this, Variant == SaListRowVariant.Rich ? "Rich" : "Simple", useTransitions: false);
}

/// <summary>
/// SaListRow stays a Button for UI Automation (invokable, announced as a button); inside an ItemsRepeater it adds its
/// position and the list total (Beacon r1 SHOULD-2: the overview summary list had -1/-1).
/// </summary>
public sealed class SaListRowAutomationPeer(SaListRow owner) : ButtonAutomationPeer(owner)
{
    protected override int GetPositionInSetCore() =>
        SaRepeaterPosition.PositionInSet((UIElement)Owner) is var position and > 0 ? position : base.GetPositionInSetCore();

    protected override int GetSizeOfSetCore() =>
        SaRepeaterPosition.SizeOfSet((UIElement)Owner) is var size and > 0 ? size : base.GetSizeOfSetCore();
}
