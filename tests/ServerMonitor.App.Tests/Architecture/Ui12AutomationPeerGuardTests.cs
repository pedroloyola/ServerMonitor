using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 PEER-01 (Prism UI12A): Panel / Border / ItemsRepeater / shapes have no automation peer, so UIA semantics put on
/// them (a name - directly or through an x:Uid resource -, IsDialog, LiveSetting, HelpText, a heading level, a landmark)
/// silently never reach a screen reader. They belong on a peer-bearing host (SaGroupHost, SaListHost) or a control.
/// Static guard; the real tree stays a runtime check (Inspect / Accessibility Insights, Beacon R-8).
/// </summary>
public sealed class Ui12AutomationPeerGuardTests
{
    private static readonly string[] PeerlessElements =
        ["Grid", "StackPanel", "Border", "ItemsRepeater", "Canvas", "RelativePanel", "VariableSizedWrapGrid", "Rectangle", "Ellipse", "Viewbox"];

    private static readonly string[] Semantics =
    [
        "AutomationProperties.Name", "AutomationProperties.IsDialog", "AutomationProperties.LiveSetting", "AutomationProperties.HelpText",
        "AutomationProperties.LandmarkType", "AutomationProperties.HeadingLevel", "AutomationProperties.FullDescription",
        "AutomationProperties.LocalizedControlType",
    ];

    /// <summary>ServerEditorPage is Sentry's in UI.12: its DialogSurface (Border + IsDialog) is handed over in the Relay log.</summary>
    private static readonly string[] HandedOver = ["Views/ServerEditorPage.xaml:DialogSurface"];

    [Fact]
    public void NoPeerlessElementCarriesAutomationSemantics()
    {
        var resourceKeys = XDocument.Load(Path.Combine(AppSourceTree.AppRoot, "Resources", "pt-PT", "Resources.resw"))
            .Descendants("data").Select(d => (string)d.Attribute("name")!).ToList();
        var failures = new List<string>();
        foreach (var file in AppSourceTree.Files(".xaml").Where(f => !f.StartsWith("Qa/", StringComparison.Ordinal)))
        {
            foreach (var element in AppSourceTree.LoadXaml(file).Descendants().Where(e => PeerlessElements.Contains(e.Name.LocalName)))
            {
                var hits = element.Attributes().Select(a => a.Name.LocalName).Where(Semantics.Contains).ToList();
                if ((string?)element.Attribute(AppSourceTree.Xaml + "Uid") is { } uid)
                {
                    hits.AddRange(resourceKeys.Where(k => k.StartsWith(uid + ".", StringComparison.Ordinal)
                        && Semantics.Any(s => k.EndsWith(s, StringComparison.Ordinal))));
                }

                var id = $"{file}:{(string?)element.Attribute(AppSourceTree.Xaml + "Name") ?? (string?)element.Attribute(AppSourceTree.Xaml + "Uid") ?? "<unnamed>"}";
                if (hits.Count > 0 && !HandedOver.Contains(id))
                {
                    failures.Add($"{id} <{element.Name.LocalName}>: {string.Join(", ", hits)}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("Controls/Primitives/SaGroupHost.cs", "new SaGroupHostAutomationPeer(this)")]
    [InlineData("Controls/Primitives/SaStatusIndicator.cs", "new SaStatusIndicatorAutomationPeer(this)")]
    [InlineData("Controls/Primitives/SaToast.cs", "new SaToastAutomationPeer(this)")]
    public void PrimitivesThatCarrySemanticsCreateTheirOwnPeer(string file, string peer) =>
        Assert.Contains("protected override AutomationPeer OnCreateAutomationPeer() => " + peer + ";", AppSourceTree.CodeWithoutComments(file), StringComparison.Ordinal);

    [Fact]
    public void GroupHostIsADialogWindowOnlyWhenAskedAndOtherwiseAGroup()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaGroupHost.cs");
        Assert.Contains("AutomationProperties.GetIsDialog(Owner) ? AutomationControlType.Window : AutomationControlType.Group", code, StringComparison.Ordinal);
        Assert.Contains("IsTabStop = false;", code, StringComparison.Ordinal);
    }

    /// <summary>The selector groups keep their keyboard contract inside the named host (UI.2 / UI.11 H01 unchanged).</summary>
    [Theory]
    [InlineData("Views/HistoryPage.xaml", "HistoryRangeSelector")]
    [InlineData("Views/WorkloadsPage.xaml", "WorkloadGlobalFilter")]
    [InlineData("Views/SettingsPage.xaml", "SettingsThemeGroup")]
    [InlineData("Controls/ServerFormControl.xaml", "ServerEditorAuthMethodGroup")]
    [InlineData("Controls/ServerFormControl.xaml", "ServerEditorJumpAuthMethodGroup")]
    public void SelectorGroupsAreNamedHostsAroundTheKeyboardGroup(string file, string uid)
    {
        var host = AppSourceTree.LoadXaml(file).Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Uid") == uid);
        Assert.Equal("SaGroupHost", host.Name.LocalName);
        var group = Assert.Single(host.Descendants(), e => e.Attributes().Any(a => a.Name.LocalName == "SaGroupNavigation.Mode"));
        Assert.Equal("SelectionFollowsFocus", group.Attributes().Single(a => a.Name.LocalName == "SaGroupNavigation.Mode").Value);
        // The sliding indicator finds its items as siblings in its own parent panel: that panel stays intact in the host.
        Assert.Contains(host.Descendants(), e => e.Attributes().Any(a => a.Name.LocalName == "SaSlidingSelection.IndicatorBrush"));
    }

    /// <summary>R1-03 (Prism r1): the step group's name is "título: estado"; its two texts are Raw so scan mode does not
    /// read them a second time.</summary>
    [Fact]
    public void TestPanelStepIsOneNamedGroupWithRawTexts()
    {
        var step = AppSourceTree.LoadXaml("Views/ServerEditorTestPanel.xaml").Descendants()
            .Single(e => e.Name.LocalName == "SaGroupHost" && e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name"));
        foreach (var text in new[] { "{Binding Title}", "{Binding StateText}" })
        {
            var block = step.Descendants().Single(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == text);
            Assert.Equal("Raw", block.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.AccessibilityView").Value);
        }
    }
}
