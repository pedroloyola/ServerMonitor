using System.Xml.Linq;
using ServerMonitor.App.Services;
using ServerMonitor.App.Controls.Primitives;

namespace ServerMonitor.App.Tests.Architecture;

public sealed class Ui6ViewContractTests
{
    private static string? A(XElement e, string name) => (string?)e.Attribute(name);
    private static XElement Named(XDocument doc, string name) => doc.Descendants().Single(e => A(e, AppSourceTree.Xaml + "Name") == name);
    private static string? A(XElement e, XName name) => (string?)e.Attribute(name);

    [Theory]
    [InlineData(1440, 208)] [InlineData(1120, 208)] [InlineData(1040, 208)]
    [InlineData(1039, 80)] [InlineData(900, 80)] [InlineData(700, 80)] [InlineData(640, 80)] [InlineData(560, 80)]
    public void Rail_ReservesOnlyItsWidth(double window, double sidebar) => Assert.Equal(sidebar, ShellLayout.SidebarWidth(window));

    [Fact]
    public void ShellOwnsOneOffset_AndModalCoversBothPresentations()
    {
        var doc = AppSourceTree.LoadXaml("MainWindow.xaml");
        var frame = Named(doc, "ContentFrame");
        Assert.Equal("1", A(frame,"Grid.Column"));
        Assert.Null(frame.Attribute("Padding")); Assert.Null(frame.Attribute("Margin"));
        Assert.Equal("208", A(Named(doc,"SidebarColumn"),"Width"));
        Assert.Equal("{ThemeResource SaSidebarMaterialBrush}", A(Named(doc,"Sidebar"),"Background"));
        Assert.Equal("{StaticResource SaShellWindowBackgroundStyle}", A(Named(doc,"WindowBackground"),"Style"));
        Assert.Contains(AppSourceTree.LoadXaml("Styles/Components/Sa.Primitives.xaml").Descendants(), e => A(e,"Value")=="{ThemeResource WindowBackdropTintBrush}");
        Assert.Same(Named(doc,"RootLayout"), Named(doc,"ModalOverlayHost").Parent);
        Assert.Same(Named(doc,"StandardRoot"), Named(doc,"FirstRunView").Parent);
        Assert.Equal("24", A(Named(doc,"ShellDragRegion"),"Height"));
    }

    [Fact]
    public void Sidebar_IsNamedNavigationWithFourReadOnlySelectionItems()
    {
        var doc = AppSourceTree.LoadXaml("Controls/SaSidebar.xaml");
        Assert.Equal("Navigation", A(doc.Root!,"AutomationProperties.LandmarkType"));
        Assert.Equal("ShellNavigation", A(doc.Root!,AppSourceTree.Xaml+"Uid"));
        Assert.Single(doc.Descendants(),e=>e.Name.LocalName=="SaListHost");
        var items=doc.Descendants().Where(e=>e.Name.LocalName=="RadioButton").ToArray();
        Assert.Equal(4,items.Length);
        var names=new[]{"Overview","Servers","History","Settings"};
        for(var i=0;i<4;i++)
        {
            Assert.Equal(names[i], A(items[i],"Tag"));
            Assert.Equal("{Binding Is"+names[i]+"Selected, Mode=OneWay}",A(items[i],"IsChecked"));
            Assert.Equal((i+1).ToString(),A(items[i],"AutomationProperties.PositionInSet"));
            Assert.Equal("4",A(items[i],"AutomationProperties.SizeOfSet"));
            Assert.Equal(i==3?"44":"46",A(items[i],"Height"));
            Assert.Equal("{StaticResource SaNavItemStyle}",A(items[i],"Style"));
        }
        Assert.Equal("FocusOnly",A(Named(doc,"NavGrid"),XName.Get("SaGroupNavigation.Mode","using:ServerMonitor.App.Controls.Primitives")));
        Assert.All(items.SelectMany(e=>e.Descendants()).Where(e=>e.Name.LocalName=="TextBlock"),e=>
        { Assert.Equal("106",A(e,"MaxWidth")); Assert.Equal("CharacterEllipsis",A(e,"TextTrimming")); });
        Assert.DoesNotContain(doc.Descendants(),e=>A(e,"Tag")=="Pro");
    }

    [Theory]
    [InlineData("Dashboard")] [InlineData("Servers")] [InlineData("ServerDetail")]
    [InlineData("History")] [InlineData("Workloads")] [InlineData("Settings")] [InlineData("SettingsData")]
    public void Pages_UseContentWidth_ExactlyOneProgrammaticHeading_AndShellThemeOwnership(string page)
    {
        var doc=AppSourceTree.LoadXaml("Views/"+page+"Page.xaml");
        Assert.DoesNotContain(doc.Descendants(),e=>e.Name.LocalName=="AdaptiveTrigger");
        var triggers=doc.Descendants().Where(e=>e.Name.LocalName=="SaContentWidthTrigger").ToArray();
        Assert.NotEmpty(triggers);
        Assert.All(triggers,e=>Assert.Equal("{Binding ActualWidth, ElementName=PageViewport}",A(e,"Width")));
        foreach(var width in new[]{480d,560d,600d,620d,640d,700d,832d,900d,912d,1040d,1120d,1232d})
            Assert.Single(triggers,e=>SaContentWidthTrigger.Matches(width,double.Parse(A(e,"MinWidth")!),A(e,"MaxWidth") is {} max?double.Parse(max):double.MaxValue));
        var heading=Assert.Single(doc.Descendants(),e=>A(e,"AutomationProperties.HeadingLevel")=="Level1");
        Assert.Equal("False",A(heading,"IsTabStop")); Assert.Equal("True",A(heading,"IsTextSelectionEnabled"));
        Assert.DoesNotContain(doc.Root!.Attributes(),a=>a.Name.LocalName=="SaThemeRefresh.IsEnabled");
    }

    [Fact]
    public void TemporaryNavigation_IsRemoved_AndReturnSlotIsMigrated()
    {
        // Build strings from parts so the zero-use search also covers this guard's production-facing literals.
        var removed=new[]{"View"+"AllButton","Dashboard"+"SettingsButton","Settings"+"BackButton","Servers"+"Breadcrumb"};
        foreach(var file in new[]{"Views/DashboardPage.xaml","Views/SettingsPage.xaml","Views/ServersPage.xaml"})
        {
            var text=File.ReadAllText(AppSourceTree.Full(file));
            Assert.All(removed,name=>Assert.DoesNotContain(name,text));
        }
        Assert.DoesNotContain("View"+"All",Enum.GetNames<ServerMonitor.App.ViewModels.OverviewReturnTarget>());
        Assert.Contains("DirectoryLink",Enum.GetNames<ServerMonitor.App.ViewModels.OverviewReturnTarget>());
        var history=AppSourceTree.LoadXaml("Views/HistoryPage.xaml");
        Assert.StartsWith("{Binding ShowDetailBack,",A(history.Descendants().Single(e=>A(e,AppSourceTree.Xaml+"Uid")=="HistoryBackButton"),"Visibility"));
        Assert.Contains("ShellPageFocus.FocusHeading(this)",AppSourceTree.CodeWithoutComments("Views/DashboardPage.xaml.cs"));
        Assert.Contains("DirectoryLinkButton.Focus(",AppSourceTree.CodeWithoutComments("Views/DashboardPage.xaml.cs"));
    }

    [Fact]
    public void BrandAndButtonVariants_PreserveVectorGeometryAndThemeTokens()
    {
        var styles=AppSourceTree.LoadXaml("Styles/Components/Sa.Primitives.xaml");
        var brand=Assert.Single(styles.Descendants(),e=>A(e,"TargetType")=="primitives:SaBrandMark" && e.Name.LocalName=="Style");
        var asset=System.IO.Path.GetFullPath(System.IO.Path.Combine(AppSourceTree.Full("App.xaml"),"..","..","..","assets","brand","Logo_Symbol_White.svg"));
        var expected="F1 "+string.Join(" ",XDocument.Load(asset).Descendants().Where(e=>e.Name.LocalName=="path").Select(e=>A(e,"d")));
        Assert.Equal(expected,A(brand.Descendants().Single(e=>e.Name.LocalName=="Path"),"Data"));
        Assert.Equal("{TemplateBinding Foreground}",A(brand.Descendants().Single(e=>e.Name.LocalName=="Path"),"Fill"));
        Assert.Contains(brand.Descendants(),e=>A(e,"Property")=="Foreground" && A(e,"Value")=="{ThemeResource SaTextBrush}");
        var buttons=AppSourceTree.LoadXaml("Styles/Components/Sa.Buttons.xaml");
        var variant=buttons.Descendants().Single(e=>A(e,AppSourceTree.Xaml+"Key")=="SaOnboardingSecondaryButtonStyle");
        Assert.Contains(variant.Descendants(),e=>A(e,"Property")=="Height" && A(e,"Value")=="{StaticResource SaOnboardingButtonHeight}");
        Assert.Contains(variant.Descendants(),e=>A(e,"Property")=="CornerRadius" && A(e,"Value")=="{StaticResource SaRadiusOnboardingButton}");
        var elevation=AppSourceTree.LoadXaml("Styles/Tokens/Elevation.xaml");
        foreach(var key in new[]{"SaWindowMaterialBrush","SaOnboardingPrimaryBrush","SaOnboardingPrimaryTextBrush","SaOnboardingBenefitBrush"})
            Assert.Equal(3,elevation.Descendants().Count(e=>A(e,AppSourceTree.Xaml+"Key")==key));
        Assert.Contains(AppSourceTree.LoadXaml("Qa/Gallery/QaNavigationPage.xaml").Descendants(),e=>e.Name.LocalName=="SaBrandMark");
    }

    [Fact]
    public void OnboardingGeometryAndEmptyStates_UseTheApprovedPrimitives()
    {
        var doc=AppSourceTree.LoadXaml("Controls/OnboardingView.xaml");
        Assert.Equal("1040",A(Named(doc,"Panel"),"MaxWidth")); Assert.Equal("780",A(Named(doc,"Panel"),"MinHeight"));
        Assert.Equal("Disabled",A(Named(doc,"Scroller"),"HorizontalScrollMode"));
        Assert.Single(doc.Descendants(),e=>A(e,"AutomationProperties.HeadingLevel")=="Level1");
        var code=AppSourceTree.CodeWithoutComments("Controls/OnboardingView.xaml.cs");
        Assert.Contains("BackButton.IsTabStop = BackButton.IsHitTestVisible = step != 1",code);
        Assert.Contains("AccessibilityView.Raw",code); Assert.Contains("Grid.SetColumnSpan(Method1,wide ? 1 : 2)",code);
        var overview=AppSourceTree.LoadXaml("Views/DashboardPage.xaml");
        var notice=Assert.Single(overview.Descendants(),e=>A(e,AppSourceTree.Xaml+"Uid")=="ConfigurationUnavailableNotice");
        Assert.Equal("Error",A(notice,"Severity")); Assert.Equal("True",A(notice,"AnnouncePolitely"));
        Assert.Equal("Polite",A(notice,"AutomationProperties.LiveSetting"));
        Assert.Contains(overview.Descendants(),e=>(A(e,"Visibility")??"").Contains("ShowAllHiddenState"));
        Assert.Contains(overview.Descendants(),e=>(A(e,"Visibility")??"").Contains("ShowFirstServerState"));
    }
}
