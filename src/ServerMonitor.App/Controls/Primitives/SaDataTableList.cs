using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.3 (Cortex M-2): the UI Automation "List" around a read-only data table. It hosts the virtualized ItemsRepeater of
/// <see cref="SaDataTableRow"/>s (whose ListItem peers report "x of N"), carries the list's localized name and is NOT a
/// tab stop itself - focus lands on the rows (one Tab stop for the whole list via the repeater's TabFocusNavigation=Once).
/// No selection, no action. Templated control, no view-model knowledge; default style in Sa.Primitives.xaml.
/// </summary>
public sealed class SaDataTableList : ContentControl
{
    public SaDataTableList()
    {
        DefaultStyleKey = typeof(SaDataTableList);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SaDataTableListAutomationPeer(this);
}

/// <summary>A List with no control patterns (no selection): the rows are read and walked, never selected.</summary>
public sealed class SaDataTableListAutomationPeer(SaDataTableList owner) : FrameworkElementAutomationPeer(owner)
{
    public const AutomationControlType ControlType = AutomationControlType.List;

    protected override AutomationControlType GetAutomationControlTypeCore() => ControlType;

    protected override string GetClassNameCore() => nameof(SaDataTableList);

    protected override object? GetPatternCore(PatternInterface patternInterface) => null;
}
