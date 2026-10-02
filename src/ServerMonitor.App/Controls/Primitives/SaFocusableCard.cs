using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.3 (Beacon F3): a READ-ONLY card the keyboard can reach - e.g. a History chart card, which in a short window would
/// otherwise be reachable only by mouse wheel. It is one Tab stop with the system focus ring, it is brought into view
/// whenever it gets focus (keyboard or UI Automation), and UI Automation sees a named Group (the consumer binds
/// AutomationProperties.Name to the card's full text summary) with no control pattern: it is read, never invoked.
/// Templated control, no view-model knowledge; default style in Sa.Primitives.xaml.
/// </summary>
public sealed class SaFocusableCard : ContentControl
{
    public SaFocusableCard()
    {
        DefaultStyleKey = typeof(SaFocusableCard);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SaFocusableCardAutomationPeer(this);

    protected override void OnGotFocus(RoutedEventArgs e)
    {
        base.OnGotFocus(e);
        // Top-aligned: in a short window a card can be taller than the viewport, and its heading must stay visible
        // (a minimal scroll would show the bottom of the card and hide its title).
        StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
    }
}

/// <summary>A Group with no control patterns: the card is announced by its name, nothing to invoke.</summary>
public sealed class SaFocusableCardAutomationPeer(SaFocusableCard owner) : FrameworkElementAutomationPeer(owner)
{
    public const AutomationControlType ControlType = AutomationControlType.Group;

    protected override AutomationControlType GetAutomationControlTypeCore() => ControlType;

    protected override string GetClassNameCore() => nameof(SaFocusableCard);

    protected override bool IsKeyboardFocusableCore() => true;

    protected override object? GetPatternCore(PatternInterface patternInterface) => null;
}
