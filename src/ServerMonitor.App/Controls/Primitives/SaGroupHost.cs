using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.12 PEER-01 (Prism UI12A): Panel / Border / ItemsRepeater have no automation peer, so an AutomationProperties.Name,
/// IsDialog or LiveSetting placed on them never reaches UI Automation (the selector group names, the import step rows,
/// the Overview "Sem problemas" panel). This host IS a Group for UIA - named by AutomationProperties.Name - around such a
/// panel, or a Window when AutomationProperties.IsDialog is set. Layout-neutral: content stretched both ways, never a
/// Tab stop. The SaListHost counterpart for lists.
/// </summary>
public sealed class SaGroupHost : ContentControl
{
    public SaGroupHost()
    {
        DefaultStyleKey = typeof(ContentControl);
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SaGroupHostAutomationPeer(this);
}

/// <summary>The Group (or, with IsDialog, Window) peer of <see cref="SaGroupHost"/>: its children are the hosted controls.</summary>
public sealed class SaGroupHostAutomationPeer(SaGroupHost owner) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationProperties.GetIsDialog(Owner) ? AutomationControlType.Window : AutomationControlType.Group;

    protected override string GetClassNameCore() => nameof(SaGroupHost);

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;
}
