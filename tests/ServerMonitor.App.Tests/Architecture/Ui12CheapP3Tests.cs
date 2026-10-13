using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>UI.12 cheap P3s from Prism UI12A (code-only): P3-14, P3-21, P3-24, P3-31. Static; UIA/Narrator stay runtime.</summary>
public sealed class Ui12CheapP3Tests
{
    /// <summary>P3-21: the sidebar landmark and its list were both "Navegação principal"; the brand text sat in the list.</summary>
    [Theory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void SidebarLandmarkAndListHaveDistinctNames(string language)
    {
        var strings = Strings(language);
        Assert.NotEqual(
            strings["ShellNavigation.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"],
            strings["ShellNavigationList.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"]);
        var brand = AppSourceTree.LoadXaml("Controls/SaSidebar.xaml").Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "BrandText");
        Assert.Equal("Raw", brand.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.AccessibilityView").Value);
    }

    /// <summary>P3-24: the Compact state title is the window's only heading (Level1, was Level2 with no H1); the loading ring is named.</summary>
    [Fact]
    public void CompactStateTitleIsLevel1AndTheRingIsNamed()
    {
        var shell = AppSourceTree.LoadXaml("Controls/CompactShell.xaml");
        var title = shell.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "StateTitleText");
        Assert.Equal("Level1", title.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.HeadingLevel").Value);
        var ring = shell.Descendants().Single(e => e.Name.LocalName == "ProgressRing");
        Assert.Equal("{x:Bind Presentation.StateTitle, Mode=OneWay}", ring.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value);
    }

    /// <summary>P3-14: the focused progress bar of "A restaurar…" is named by the message.</summary>
    [Fact]
    public void RestoringProgressBarIsNamed() =>
        Assert.Contains("AutomationProperties.SetName(progress, message);", AppSourceTree.CodeWithoutComments("Services/BackupRestoreDialogService.cs"), StringComparison.Ordinal);

    /// <summary>P3-31: pt-PT says "host" (67 strings) and "confiável" everywhere - no "anfitrião" / "fidedigna" left.</summary>
    [Fact]
    public void PortugueseHostKeyWordingIsUniform()
    {
        var values = Strings("pt-PT").Values.ToList();
        Assert.DoesNotContain(values, v => v.Contains("anfitri", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(values, v => v.Contains("fidedign", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> Strings(string language) =>
        XDocument.Load(Path.Combine(AppSourceTree.AppRoot, "Resources", language, "Resources.resw"))
            .Descendants("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!, StringComparer.Ordinal);
}
