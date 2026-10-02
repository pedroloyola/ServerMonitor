using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.2 empty state (Figma 112:14088 / 112:14182): icon 40 muted, title 24/30 Semibold, message 14 muted centred
/// (max 560), optional action (Content, normally a SaSolidButtonStyle button 200x40), gap 16.
/// </summary>
public sealed class SaEmptyState : ContentControl
{
    public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
        nameof(IconData), typeof(string), typeof(SaEmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SaEmptyState), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(SaEmptyState), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TitleHeadingLevelProperty = DependencyProperty.Register(
        nameof(TitleHeadingLevel), typeof(AutomationHeadingLevel), typeof(SaEmptyState), new PropertyMetadata(AutomationHeadingLevel.Level2));

    public SaEmptyState()
    {
        DefaultStyleKey = typeof(SaEmptyState);
        IsTabStop = false;
    }

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

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>UIA heading level of the title (Beacon L3): 2 for a page state, 3 for a state inside a level-2 section.</summary>
    public AutomationHeadingLevel TitleHeadingLevel
    {
        get => (AutomationHeadingLevel)GetValue(TitleHeadingLevelProperty);
        set => SetValue(TitleHeadingLevelProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SaEmptyStateAutomationPeer(this);
}

/// <summary>
/// Beacon L2: a Group named by the state TITLE. Without it the name was derived from the content - the action button - so
/// the state (and the ScrollViewer around it) was announced as "Limpar pesquisa".
/// </summary>
public sealed class SaEmptyStateAutomationPeer(SaEmptyState owner) : FrameworkElementAutomationPeer(owner)
{
    public const AutomationControlType ControlType = AutomationControlType.Group;

    protected override AutomationControlType GetAutomationControlTypeCore() => ControlType;

    protected override string GetClassNameCore() => nameof(SaEmptyState);

    protected override string GetNameCore() =>
        AutomationProperties.GetName(Owner) is { Length: > 0 } explicitName ? explicitName : ((SaEmptyState)Owner).Title ?? string.Empty;
}
