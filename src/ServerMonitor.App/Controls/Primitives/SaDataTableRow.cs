using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.3 (Boss decision on difference 14 / Cortex M-2): one READ-ONLY data-table row that the keyboard can reach. It is
/// focusable (system focus ring) so a list host with <c>TabFocusNavigation="Once"</c> + <c>XYFocusKeyboardNavigation="Enabled"</c>
/// gives "Tab enters the list, arrows walk the rows" without a tab stop per row. It exposes no action: no click, no
/// Invoke/Toggle/Selection pattern - UI Automation sees a ListItem (with its position "x of N" in the list) whose name is
/// the full row summary (the consumer binds AutomationProperties.Name). Templated control, no view-model knowledge;
/// default style in Sa.Primitives.xaml.
/// </summary>
public sealed class SaDataTableRow : ContentControl
{
    public SaDataTableRow()
    {
        DefaultStyleKey = typeof(SaDataTableRow);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SaDataTableRowAutomationPeer(this);

    /// <summary>
    /// A focused row is always brought into view - by keyboard (Tab/arrows) AND by programmatic/UIA focus, which WinUI does
    /// not scroll to on its own. In the stacked layouts the request bubbles through the disabled card ScrollViewer to the
    /// page ScrollViewer (Cortex M-3 / Prism R2).
    /// </summary>
    protected override void OnGotFocus(RoutedEventArgs e)
    {
        base.OnGotFocus(e);
        StartBringIntoView();
    }
}

/// <summary>A ListItem with no control patterns: rows are read and walked, never invoked.</summary>
public sealed class SaDataTableRowAutomationPeer(SaDataTableRow owner) : FrameworkElementAutomationPeer(owner)
{
    /// <summary>The UIA type, kept as a constant so it is decidable without a XAML runtime.</summary>
    public const AutomationControlType ControlType = AutomationControlType.ListItem;

    protected override AutomationControlType GetAutomationControlTypeCore() => ControlType;

    protected override string GetClassNameCore() => nameof(SaDataTableRow);

    protected override object? GetPatternCore(PatternInterface patternInterface) => null;

    /// <summary>1-based position among the list's items (not only the realized ones), so a reader says "x of N".</summary>
    protected override int GetPositionInSetCore() =>
        Repeater() is { } repeater && repeater.GetElementIndex((UIElement)Owner) is var index and >= 0 ? index + 1 : base.GetPositionInSetCore();

    protected override int GetSizeOfSetCore() =>
        Repeater()?.ItemsSourceView?.Count is { } count and > 0 ? count : base.GetSizeOfSetCore();

    private ItemsRepeater? Repeater()
    {
        for (var current = VisualTreeHelper.GetParent(Owner); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ItemsRepeater repeater)
            {
                return repeater;
            }
        }

        return null;
    }
}
