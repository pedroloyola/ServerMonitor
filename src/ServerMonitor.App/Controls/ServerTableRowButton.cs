using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Controls.Primitives;

namespace ServerMonitor.App.Controls;

/// <summary>
/// UI.4 Servidores table row (Figma 112:1443): ONE target per server (Enter/Space/click → interim page). A Button so it
/// is invokable and announced as a button, plus "x of N" over the whole virtualized list (Beacon r1 SHOULD-2). The
/// look is the page's ServerRowButtonStyle.
/// </summary>
public sealed partial class ServerTableRowButton : Button
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ServerTableRowButtonAutomationPeer(this);
}

public sealed class ServerTableRowButtonAutomationPeer(ServerTableRowButton owner) : ButtonAutomationPeer(owner)
{
    protected override int GetPositionInSetCore() =>
        SaRepeaterPosition.PositionInSet((UIElement)Owner) is var position and > 0 ? position : base.GetPositionInSetCore();

    protected override int GetSizeOfSetCore() =>
        SaRepeaterPosition.SizeOfSet((UIElement)Owner) is var size and > 0 ? size : base.GetSizeOfSetCore();
}
