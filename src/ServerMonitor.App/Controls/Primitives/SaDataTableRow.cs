using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.3 (Boss decision on difference 14): one READ-ONLY data-table row that the keyboard can reach. It is focusable
/// (system focus ring) so a list host with <c>TabFocusNavigation="Once"</c> + <c>XYFocusKeyboardNavigation="Enabled"</c>
/// gives "Tab enters the list, arrows walk the rows" without a tab stop per row. It exposes no action: no click, no
/// Invoke/Toggle/Selection pattern - UI Automation sees a ListItem whose name is the full row summary (the consumer
/// binds AutomationProperties.Name). Templated control, no view-model knowledge; default style in Sa.Primitives.xaml.
/// </summary>
public sealed class SaDataTableRow : ContentControl
{
    public SaDataTableRow()
    {
        DefaultStyleKey = typeof(SaDataTableRow);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SaDataTableRowAutomationPeer(this);
}

/// <summary>A ListItem with no control patterns: rows are read and walked, never invoked.</summary>
public sealed class SaDataTableRowAutomationPeer(SaDataTableRow owner) : FrameworkElementAutomationPeer(owner)
{
    /// <summary>The UIA type, kept as a constant so it is decidable without a XAML runtime.</summary>
    public const AutomationControlType ControlType = AutomationControlType.ListItem;

    protected override AutomationControlType GetAutomationControlTypeCore() => ControlType;

    protected override string GetClassNameCore() => nameof(SaDataTableRow);

    protected override object? GetPatternCore(PatternInterface patternInterface) => null;
}
