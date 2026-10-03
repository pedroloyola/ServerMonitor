using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.5 Beacon C2 R2-N1: a content host that IS a list for UI Automation - control type List, named by
/// AutomationProperties.Name - around an ItemsRepeater (which has no automation peer). The repeater keeps its
/// virtualization and the items keep their own Tab stops and PositionInSet / SizeOfSet. Never a Tab stop itself.
/// </summary>
public sealed class SaListHost : ContentControl
{
    public SaListHost()
    {
        DefaultStyleKey = typeof(ContentControl);
        IsTabStop = false;
        HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SaListHostAutomationPeer(this);
}

/// <summary>The List peer of <see cref="SaListHost"/>: its children are the item controls.</summary>
public sealed class SaListHostAutomationPeer(SaListHost owner) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

    protected override string GetClassNameCore() => nameof(SaListHost);

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;
}
