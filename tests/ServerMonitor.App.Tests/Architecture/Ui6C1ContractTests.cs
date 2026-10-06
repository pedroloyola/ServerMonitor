using System.Xml.Linq;
namespace ServerMonitor.App.Tests.Architecture;
public sealed class Ui6C1ContractTests
{
    private static string? A(XElement e, string name) => (string?)e.Attribute(name);
    private static XElement Key(XDocument doc, string key) => doc.Descendants().First(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == key);
    [Fact]
    public void CompactAndOnboardingPadding_ClearTheNativeCaptionBand()
    {
        var doc = AppSourceTree.LoadXaml("Styles/Tokens/Spacing.xaml");
        foreach (var key in new[] { "SaPagePadding", "SaPagePaddingCompact", "SaOnboardingPadding" })
        {
            var parts = Key(doc,key).Value.Split(',');
            Assert.True(double.Parse(parts.Length == 1 ? parts[0] : parts[1]) >= 40, key);
        }
    }
    [Fact]
    public void EmptyBlocks_CentreWithinFiniteContainer_AndIconsUseLiveThemeResources()
    {
        var doc = AppSourceTree.LoadXaml("Views/DashboardPage.xaml");
        foreach (var state in new[] { "ShowFirstServerState", "ShowAllHiddenState" })
        {
            var container = doc.Descendants().Single(e => A(e,"Visibility") == "{x:Bind ViewModel."+state+", Mode=OneWay}");
            Assert.Equal("Grid", container.Name.LocalName); Assert.Equal("620",A(container,"MinHeight"));
            Assert.Equal("Center", A(Assert.Single(container.Elements()),"VerticalAlignment"));
            var icon = container.Descendants().First(e=>e.Name.LocalName=="SaIcon");
            Assert.Equal("48",A(icon,"Size"));
            Assert.Equal("{ThemeResource SaTextBrush}",A(icon,"Foreground"));
        }
        var themes = AppSourceTree.LoadXaml("Styles/Tokens/Color.Semantic.xaml");
        // A ThemeResource expression is re-evaluated on live theme changes; all three palettes must define it.
        Assert.Equal(3, themes.Descendants().Count(e=>(string?)e.Attribute(AppSourceTree.Xaml+"Key")=="SaTextBrush"));
    }
    [Fact]
    public void SidebarEdgeAndButtonBorders_AreConsumedAndDefinedInEveryTheme()
    {
        var elevation=AppSourceTree.LoadXaml("Styles/Tokens/Elevation.xaml");
        foreach(var key in new[]{"SaSidebarEdgeBrush","SaOnboardingSecondaryFillBrush","SaOnboardingButtonBorderBrush","SaOnboardingCardBorderBrush"})
            Assert.Equal(3,elevation.Descendants().Count(e=>(string?)e.Attribute(AppSourceTree.Xaml+"Key")==key));
        Assert.Contains(AppSourceTree.LoadXaml("MainWindow.xaml").Descendants(), e=>A(e,"Background")=="{ThemeResource SaSidebarEdgeBrush}" && A(e,"Width")=="1");
        var secondary=Key(AppSourceTree.LoadXaml("Styles/Components/Sa.Buttons.xaml"),"SaOnboardingSecondaryButtonStyle");
        Assert.Contains(secondary.Elements(),e=>A(e,"Property")=="BorderThickness" && A(e,"Value")=="{StaticResource SaBorderThickness}");
        Assert.Contains(secondary.Elements(),e=>A(e,"Property")=="Background" && A(e,"Value")=="{ThemeResource SaOnboardingSecondaryFillBrush}");
    }
    [Theory]
    [InlineData("Dashboard")] [InlineData("Servers")] [InlineData("ServerDetail")]
    [InlineData("History")] [InlineData("Workloads")] [InlineData("Settings")] [InlineData("SettingsData")]
    public void PageHeading_UsesContentPadding_WithoutEmptyLeadingColumnGap(string page)
    {
        var doc=AppSourceTree.LoadXaml("Views/"+page+"Page.xaml");
        var heading=doc.Descendants().Single(e=>A(e,"AutomationProperties.HeadingLevel")=="Level1");
        Assert.Contains(heading.Ancestors(),e=>A(e,"Padding")=="{StaticResource SaPagePadding}");
        if(page=="Settings") Assert.Equal("0",A(heading.Parent!,"Grid.Column"));
        if(page=="History")
        {
            Assert.Equal("StackPanel", heading.Parent!.Parent!.Name.LocalName);
            Assert.Null(A(heading.Parent!, "Grid.Column"));
            var back = heading.Parent.Parent.Elements().Single(e => e.Name.LocalName == "Button");
            Assert.Equal("22", A(back, "Height"));
        }
        // Detail's identity icon and Data/Workloads back affordances intentionally precede H1 within the same column.
    }
    /// <summary>
    /// UI.7 B-3/B-5 (rewritten from the UI.6 modal contract): the editor's opener is captured when the session opens a
    /// visit, remembered only when the user leaves without saving, and the view model is disposed in a finally that also
    /// ends the visit. The shared dialog return focus (still used by the remaining dialogs) keeps its UI.6 rules.
    /// </summary>
    [Fact]
    public void EditorSession_CapturesOpenerFirst_AndAlwaysEndsTheVisit()
    {
        var code=AppSourceTree.CodeWithoutComments("Services/ServerEditorSession.cs");
        Assert.True(code.IndexOf("CaptureTrigger()",StringComparison.Ordinal)<code.IndexOf("_navigation.GoToServerEditor(",StringComparison.Ordinal));
        Assert.Contains("visit.ViewModel?.Dispose();",code);
        Assert.Contains("visit.Completion.TrySetResult();",code);
        Assert.True(code.IndexOf("_returnFocus.Remember(",StringComparison.Ordinal)>code.IndexOf("_navigation.LeaveCurrentPageThen(",StringComparison.Ordinal));
        // UI.7C (B-22, Cortex n-7): DialogReturnFocus had no caller left (the editor became a page with its own return slot).
        Assert.False(File.Exists(AppSourceTree.Full("Services/DialogReturnFocus.cs")));
    }
    [Theory]
    [InlineData("Dashboard", "TakeReturnFocus()")]
    [InlineData("Servers", "TakeReturnFocusIndex()")]
    [InlineData("ServerDetail", "TakeReturnFocus()")]
    public void ReturnSlot_IsConsumedBeforeKeepingSidebar(string page, string take)
    {
        var code=AppSourceTree.CodeWithoutComments("Views/"+page+"Page.xaml.cs");
        Assert.True(code.IndexOf(take,StringComparison.Ordinal)<code.IndexOf("if (ShellPageFocus.GetKeepSidebar(this)) return;",StringComparison.Ordinal));
    }
    [Fact]
    public void Sidebar_ConnectsProjectionAndRailOnlyTooltip()
    {
        var code=AppSourceTree.CodeWithoutComments("Controls/SaSidebar.xaml.cs");
        Assert.Contains(".IsChecked = selected",code);
        Assert.Contains("DataContextChanged +=",code);
        Assert.Contains("_selection.Bind(DataContext as ShellViewModel)",code);
        Assert.Contains("_selection.ActivateKey(args.Key",code);
        Assert.Contains("ToolTipService.SetToolTip(item, rail ?",code);
    }
    [Fact]
    public void NoticeHidesEmptyTitle_AndHeadingFocusBoundsItsContent()
    {
        Assert.Contains("string.IsNullOrWhiteSpace(Title) ? Visibility.Collapsed : Visibility.Visible",AppSourceTree.CodeWithoutComments("Controls/Primitives/SaInlineNotice.cs"));
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Left",AppSourceTree.CodeWithoutComments("Controls/Primitives/SaHeadingHost.cs"));
    }
}
