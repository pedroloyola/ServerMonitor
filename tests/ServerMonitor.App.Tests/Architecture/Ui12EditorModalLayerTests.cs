using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Automation.Peers;
using ServerMonitor.App.Views;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 UI12A-MODAL-01 (Prism, P2). (1) With the editor's in-page modal layer open, the form behind it stayed in the UIA
/// tree (only hit-testing and IsEnabled were turned off), so a screen reader's browse mode walked out of the dialog. It is now
/// AccessibilityView Raw exactly while the layer is open - and the layer itself is NOT inside it. (2) "Confiar e …" was
/// disabled while it had the focus, throwing the focus out of the layer (Esc then no longer reached the page); the focus now
/// moves to the safe button first. WinUI types: the rule is pure, the wiring and order are pinned by source. Runtime (UIA
/// tree with the layer open; Accept → focus on Cancelar → Esc works) is Beacon's (Prism R-2).
/// </summary>
public sealed partial class Ui12EditorModalLayerTests
{
    [Fact]
    public void ThePageIsRaw_ExactlyWhileTheLayerIsOpen()
    {
        Assert.Equal(AccessibilityView.Raw, ServerEditorPage.PageAccessibilityView(interactive: false));
        Assert.Equal(AccessibilityView.Content, ServerEditorPage.PageAccessibilityView(interactive: true));
    }

    [Fact]
    public void TheDialogLayer_IsNotInsideTheContentThatGoesRaw()
    {
        var xaml = AppSourceTree.LoadXaml("Views/ServerEditorPage.xaml");
        var scroll = xaml.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "PageScroll");

        Assert.DoesNotContain(scroll.DescendantsAndSelf(), e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") is "DialogLayer" or "DialogContent");
        Assert.Null(scroll.Attribute("AutomationProperties.AccessibilityView")); // declared default (Content) is what "restore" returns to
    }

    [Fact]
    public void TheWiring_SetsTheViewWithInteractivity_AndMovesFocusBeforeDisablingAccept()
    {
        var page = AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs");

        Assert.Matches(SetPageInteractiveSetsTheView(), page);
        Assert.Matches(FocusSafeBeforeDisablingAccept(), page);
    }

    [GeneratedRegex(@"private void SetPageInteractive\(bool interactive\)\s*\{\s*PageRoot\.IsHitTestVisible = interactive;\s*PageScroll\.IsEnabled = interactive;\s*AutomationProperties\.SetAccessibilityView\(PageScroll, PageAccessibilityView\(interactive\)\);\s*\}")]
    private static partial Regex SetPageInteractiveSetsTheView();

    [GeneratedRegex(@"_acceptingTrust = true;\s*TrustPanel\.FocusSafeButton\(\);\s*TrustPanel\.SetWorking\(true, acceptAllowed: false\);")]
    private static partial Regex FocusSafeBeforeDisablingAccept();
}
