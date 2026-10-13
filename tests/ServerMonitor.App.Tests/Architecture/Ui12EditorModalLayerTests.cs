using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.UI.Xaml.Automation.Peers;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;

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

    // ---- B12F-01 follow-up (Cortex N1 / Prism P3): the inert page raises no live-region change over the open layer ----

    [Theory]
    [InlineData(ServerEditorLayer.None, true)]
    [InlineData(ServerEditorLayer.Test, false)]
    [InlineData(ServerEditorLayer.Trust, false)]
    [InlineData(ServerEditorLayer.Import, false)]
    public void ThePageAnnounces_OnlyWithNoLayerOpen(ServerEditorLayer layer, bool announces) =>
        Assert.Equal(announces, ServerEditorPage.PageAnnounces(layer));

    [Fact]
    public void BothLiveRegions_AreWrittenOnlyThroughTheGate()
    {
        var page = AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs");

        // ActionHint (polite, SaAnnounce.Changes): its one writer sits inside the gate.
        Assert.Single(HintWrites().Matches(page));
        Assert.Matches(HintBehindTheGate(), page);
        // EditorSubtitle (assertive): the initial text (no layer exists yet) and SetSubtitle, which starts with the gate.
        Assert.Equal(2, SubtitleWrites().Matches(page).Count);
        Assert.Contains("EditorSubtitle.Text = _subtitle;", page, StringComparison.Ordinal);
        Assert.Matches(SubtitleBehindTheGate(), page);
    }

    [Fact]
    public void ClosingTheLayer_BringsBothLiveRegionsUpToDate_BeforeTheFocusReturns()
    {
        var page = AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs");
        var start = page.IndexOf("private void HideLayer()", StringComparison.Ordinal);
        var hide = page[start..page.IndexOf("internal static bool LayerReturnsToHeaderButton", start, StringComparison.Ordinal)];

        int At(string text) => hide.IndexOf(text, StringComparison.Ordinal);
        var none = At("_layer = ServerEditorLayer.None;");
        var subtitle = At("SetSubtitle(SubtitleText(viewModel));");
        var hint = At("UpdateActionState();");
        var focus = At("FocusWhenLaidOut(");
        Assert.True(none > 0 && subtitle > none && hint > subtitle && focus > hint, "order: layer None, subtitle, hint, then the focus");
    }

    [GeneratedRegex(@"\bActionHint\.Text\s*=")]
    private static partial Regex HintWrites();

    [GeneratedRegex(@"\bEditorSubtitle\.Text\s*=")]
    private static partial Regex SubtitleWrites();

    [GeneratedRegex(@"if \(PageAnnounces\(_layer\)\)\s*\{\s*var hint = _controller\.ActionHintKey\(\);\s*ActionHint\.Text = ")]
    private static partial Regex HintBehindTheGate();

    [GeneratedRegex(@"private void SetSubtitle\(string text\)\s*\{\s*if \(!PageAnnounces\(_layer\) \|\| ")]
    private static partial Regex SubtitleBehindTheGate();

    // ---- Atlas A-B12F-01: the IsInert setter (transition + notification branch). STATIC proof: App.Tests has no XAML
    // runtime, so SaInertHost cannot be instantiated here and no UIA StructureChanged event is observed; the received event
    // and the real tree are runtime checks (QA). The whole body is pinned so that any change to the state write, the
    // repeated-value guard, the order or the listener condition fails. ----

    [Fact]
    public void TheIsInertSetter_IsExactly_GuardRepeat_WriteState_ThenNotifyWhenAPeerExistsAndSomeoneListens()
    {
        var code = Normalized(AppSourceTree.CodeWithoutComments("Controls/Primitives/SaInertHost.cs"));

        Assert.Contains(Normalized("""
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
            """), code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSetter_WritesTheStateThePeerReads_ExactlyOnce_AndNeverNegatesTheListenerCheck()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaInertHost.cs");

        // The one field the peer's rule reads (through IsInert) is written in one place: the setter, from value.
        Assert.Single(StateWrites().Matches(code));
        Assert.Contains("_isInert = value;", code, StringComparison.Ordinal);
        Assert.Contains("get => _isInert;", code, StringComparison.Ordinal);
        Assert.Contains("Children(((SaInertHost)Owner).IsInert, base.GetChildrenCore)", code, StringComparison.Ordinal);
        // The event is raised only while a UIA client listens (and only then): never the negated check.
        Assert.Single(ListenerChecks().Matches(code));
        Assert.DoesNotContain("!AutomationPeer.ListenerExists", code, StringComparison.Ordinal);
        Assert.Single(StructureEvents().Matches(code));
    }

    private static string Normalized(string code) => WhiteSpace().Replace(code, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"\b_isInert\s*=(?!=)")]
    private static partial Regex StateWrites();

    [GeneratedRegex(@"ListenerExists\(")]
    private static partial Regex ListenerChecks();

    [GeneratedRegex(@"RaiseStructureChangedEvent\(")]
    private static partial Regex StructureEvents();
}
