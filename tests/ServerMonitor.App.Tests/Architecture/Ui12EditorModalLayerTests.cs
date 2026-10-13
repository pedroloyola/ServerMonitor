using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.UI.Xaml.Automation.Peers;
using ServerMonitor.App.Controls.Primitives;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 UI12A-MODAL-01 / B12F-01 (Prism, Beacon, P2). (1) With the editor's in-page modal layer open, the form behind it
/// must not be reachable by UI Automation. AccessibilityView Raw on PageScroll (e033b50) only filtered that one node out of
/// the Control / Content views: its descendants re-parented to the nearest ancestor peer and stayed reachable (Beacon,
/// ControlViewWalker). The page now sits in an SaInertHost whose peer has NO children while the layer is open - the subtree
/// leaves the raw tree, so every view. (2) "Confiar e …" was disabled while it had the focus, throwing the focus out of the
/// layer; the focus now moves to the safe button first. WinUI types need the XAML runtime: the peer's child rule is pure,
/// the wiring is pinned by source. Runtime (UIA ControlViewWalker / RawViewWalker with the layer open, restored on close;
/// Narrator) is a QA-window check.
/// </summary>
public sealed partial class Ui12EditorModalLayerTests
{
    [Fact]
    public void AnInertHostHasNoChildren_AndDoesNotEvenWalkTheVisualTree()
    {
        var walked = false;
        var children = SaInertHostAutomationPeer.Children(inert: true, () =>
        {
            walked = true;
            return new List<AutomationPeer>();
        });

        Assert.NotNull(children);
        Assert.Empty(children);
        Assert.False(walked);
    }

    [Fact]
    public void AnInteractiveHostExposesExactlyTheDefaultChildren()
    {
        var visual = new List<AutomationPeer>();
        Assert.Same(visual, SaInertHostAutomationPeer.Children(inert: false, () => visual));
        Assert.Null(SaInertHostAutomationPeer.Children(inert: false, () => null)); // the base "no children" passes through
    }

    [Fact]
    public void ThePeerIsLayoutOnly_UsesTheRule_AndTheSwitchInvalidatesTheChildren()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaInertHost.cs");

        Assert.Contains("protected override AutomationPeer OnCreateAutomationPeer() => new SaInertHostAutomationPeer(this);", code, StringComparison.Ordinal);
        Assert.Contains("protected override bool IsControlElementCore() => false;", code, StringComparison.Ordinal);
        Assert.Contains("protected override bool IsContentElementCore() => false;", code, StringComparison.Ordinal);
        Assert.Contains("GetChildrenCore() => Children(((SaInertHost)Owner).IsInert, base.GetChildrenCore);", code, StringComparison.Ordinal);
        Assert.Contains("peer.RaiseStructureChangedEvent(AutomationStructureChangeType.ChildrenInvalidated, peer);", code, StringComparison.Ordinal);
        Assert.Contains("IsTabStop = false;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageContent_IsTheInertHostsOnlyChild_AndTheDialogLayerIsOutsideIt()
    {
        var xaml = AppSourceTree.LoadXaml("Views/ServerEditorPage.xaml");
        var host = Named(xaml, "PageHost");

        Assert.Equal("SaInertHost", host.Name.LocalName);
        Assert.Equal("PageScroll", (string?)Assert.Single(host.Elements()).Attribute(AppSourceTree.Xaml + "Name"));
        Assert.Contains(host.Descendants(), e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") is "PageRoot" or "HeaderButton");
        Assert.DoesNotContain(host.Descendants(), e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") is "DialogLayer" or "DialogContent");
        Assert.Equal("EditorHost", (string?)host.Parent!.Attribute(AppSourceTree.Xaml + "Name"));
        // Raw on the page did not hide its descendants (B12F-01); nothing on the host path relies on it any more.
        Assert.Null(Named(xaml, "PageScroll").Attribute("AutomationProperties.AccessibilityView"));
        Assert.Null(host.Attribute("AutomationProperties.AccessibilityView"));
    }

    [Fact]
    public void TheWiring_MakesThePageInertWithInteractivity_AndMovesFocusBeforeDisablingAccept()
    {
        var page = AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs");

        Assert.Matches(SetPageInteractiveSetsInertness(), page);
        Assert.DoesNotContain("SetAccessibilityView(PageScroll", page, StringComparison.Ordinal);
        Assert.Single(InertWrites().Matches(page)); // only SetPageInteractive switches it: open (false) / every close (true)
        Assert.Matches(FocusSafeBeforeDisablingAccept(), page);
    }

    private static XElement Named(XDocument xaml, string name) =>
        xaml.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == name);

    [GeneratedRegex(@"private void SetPageInteractive\(bool interactive\)\s*\{\s*PageRoot\.IsHitTestVisible = interactive;\s*PageScroll\.IsEnabled = interactive;\s*PageHost\.IsInert = !interactive;\s*\}")]
    private static partial Regex SetPageInteractiveSetsInertness();

    [GeneratedRegex(@"\bIsInert\s*=")]
    private static partial Regex InertWrites();

    [GeneratedRegex(@"_acceptingTrust = true;\s*TrustPanel\.FocusSafeButton\(\);\s*TrustPanel\.SetWorking\(true, acceptAllowed: false\);")]
    private static partial Regex FocusSafeBeforeDisablingAccept();
}
