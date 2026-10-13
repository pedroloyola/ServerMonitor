using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.12 B12F-01: the content behind an in-page modal layer, made inert for UI Automation while the layer is open.
/// AccessibilityView Raw on the content was not enough: Raw only filters that one peer out of the Control / Content views
/// (the views are filters over the raw tree, UIA tree overview), so its descendants re-parented to the nearest ancestor
/// peer and stayed reachable. The raw tree itself is what a peer's GetChildrenCore returns (FrameworkElementAutomationPeer
/// walks the visual tree by default), so this host's peer returns no children while <see cref="IsInert"/> - the whole
/// subtree leaves every view - and raises StructureChanged (ChildrenInvalidated) on each switch. When not inert it is
/// layout-only (neither a control nor a content element) and exposes its visual children as before. Layout-neutral, never
/// a Tab stop. Keyboard and pointer inertness stay with the page (IsEnabled / IsHitTestVisible).
/// </summary>
public sealed class SaInertHost : ContentControl
{
    private bool _isInert;

    public SaInertHost()
    {
        DefaultStyleKey = typeof(ContentControl);
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>True while a modal layer covers this content: nothing under it is in the UIA tree.</summary>
    public bool IsInert
    {
        get => _isInert;
        set
        {
            if (_isInert == value)
            {
                return;
            }

            _isInert = value;
            if (FrameworkElementAutomationPeer.FromElement(this) is { } peer
                && AutomationPeer.ListenerExists(AutomationEvents.StructureChanged))
            {
                peer.RaiseStructureChangedEvent(AutomationStructureChangeType.ChildrenInvalidated, peer);
            }
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SaInertHostAutomationPeer(this);
}

/// <summary>The peer of <see cref="SaInertHost"/>: layout-only, and childless while the host is inert.</summary>
public sealed class SaInertHostAutomationPeer(SaInertHost owner) : FrameworkElementAutomationPeer(owner)
{
    protected override string GetClassNameCore() => nameof(SaInertHost);

    protected override bool IsControlElementCore() => false;

    protected override bool IsContentElementCore() => false;

    protected override IList<AutomationPeer>? GetChildrenCore() => Children(((SaInertHost)Owner).IsInert, base.GetChildrenCore);

    /// <summary>Inert: no children (the visual tree is not even walked). Otherwise exactly the default children.</summary>
    internal static IList<AutomationPeer>? Children(bool inert, Func<IList<AutomationPeer>?> visualChildren) =>
        inert ? [] : visualChildren();
}
