using System.Globalization;
using System.Xml.Linq;
using ServerMonitor.App.Views;
namespace ServerMonitor.App.Tests.Architecture;

public sealed class Ui6C2ContractTests
{
    private static string? A(XElement e, string name) => (string?)e.Attribute(name);
    private static XElement Named(XDocument d, string name) => d.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == name);

    [Theory]
    [InlineData(464)] [InlineData(544)] [InlineData(639)]
    public void History_NarrowGeometry_FitsThePaddedContent(double contentWidth)
    {
        var d = AppSourceTree.LoadXaml("Views/HistoryPage.xaml");
        var narrow = Named(d, "Narrow");
        var setters = narrow.Descendants().Where(e => e.Name.LocalName == "Setter").ToDictionary(e => A(e,"Target")!, e => A(e,"Value")!);
        Assert.Equal("0", setters.GetValueOrDefault("ServerSelector.MinWidth", "240"));
        Assert.Equal("NaN", setters["ServerSelector.Width"]);
        Assert.Equal("Stretch", setters["ServerSelector.HorizontalAlignment"]);
        Assert.Equal("*", setters.GetValueOrDefault("ControlsFirstColumn.Width", "Auto"));
        Assert.Equal("3", setters.GetValueOrDefault("RangeTrack.(Grid.ColumnSpan)", "1"));
        var group = Named(d, "RangeItems");
        Assert.Equal("Grid", group.Name.LocalName);
        Assert.Equal("4", A(group,"ColumnSpacing"));
        Assert.Equal("436", A(Named(d,"RangeTrack"),"Width"));
        var usable = contentWidth - 48;
        var track = HistoryPage.RangeTrackWidth(usable);
        Assert.True(track <= usable);
        Assert.True((track - 8 - 4 * 4) / 5 >= 74);
        Assert.Contains("RangeTrack.Width = RangeTrackWidth(ActualWidth - PageRoot.Padding.Left - PageRoot.Padding.Right)",AppSourceTree.CodeWithoutComments("Views/HistoryPage.xaml.cs"));
    }

    [Theory]
    [InlineData(436, 82.4)] [InlineData(416, 78.4)] [InlineData(496, 82.4)] [InlineData(394, 74)] [InlineData(1000, 82.4)]
    public void History_ItemsFillEqualHitAreas_InWideAndNarrow(double usableWidth, double expected)
    {
        var d = AppSourceTree.LoadXaml("Views/HistoryPage.xaml");
        var group = Named(d, "RangeItems");
        Assert.Equal("Grid", group.Name.LocalName);
        var columns = group.Element(group.Name.Namespace+"Grid.ColumnDefinitions")!.Elements().ToArray();
        Assert.Equal(5, columns.Length);
        Assert.All(columns, column => Assert.Equal("*",A(column,"Width")));
        Assert.Equal("4",A(group,"ColumnSpacing"));
        var forms = AppSourceTree.LoadXaml("Styles/Components/Sa.Forms.xaml");
        var trackStyle = forms.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml+"Key") == "SaSegmentedRectTrackStyle");
        Assert.Contains(trackStyle.Elements(), e => A(e,"Property") == "Padding" && A(e,"Value") == "4");
        var widths = new List<double>();
        foreach (var item in group.Elements().Where(e => e.Name.LocalName == "RadioButton"))
        {
            var styleKey = A(item,"Style")!.Replace("{StaticResource ", "").TrimEnd('}');
            var style = forms.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml+"Key") == styleKey);
            var setters = style.Elements().ToDictionary(e => A(e,"Property")!, e => A(e,"Value")!);
            Assert.Equal("Stretch", A(item,"HorizontalAlignment") ?? setters.GetValueOrDefault("HorizontalAlignment"));
            Assert.Equal("Center", A(item,"HorizontalContentAlignment") ?? setters.GetValueOrDefault("HorizontalContentAlignment"));
            Assert.Null(A(item,"Width")); Assert.Null(A(item,"Margin"));
            Assert.Equal("74", A(item,"MinWidth"));
            Assert.Equal(widths.Count.ToString(CultureInfo.InvariantCulture), A(item,"Grid.Column"));
            widths.Add((HistoryPage.RangeTrackWidth(usableWidth) - 8 - 4 * 4) / columns.Length);
        }
        Assert.Equal(5, widths.Count);
        Assert.All(widths, width => { Assert.Equal(expected,width,5); Assert.True(width >= 74); });
    }

    [Fact]
    public void SidebarVeil_IsPaintedBelowTheControl_AndIsTransparentInHighContrast()
    {
        var d = AppSourceTree.LoadXaml("MainWindow.xaml");
        var sidebar = Named(d,"Sidebar");
        Assert.Null(A(sidebar,"Background"));
        var veil = sidebar.ElementsBeforeSelf().Single(e => A(e,"Background") == "{ThemeResource SaSidebarMaterialBrush}");
        Assert.Equal("Border", veil.Name.LocalName);
        Assert.Equal("False", A(veil,"IsHitTestVisible"));
        var elevation = AppSourceTree.LoadXaml("Styles/Tokens/Elevation.xaml");
        var hc = elevation.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml+"Key") == "HighContrast");
        Assert.Equal("Transparent", A(hc.Elements().Single(e => (string?)e.Attribute(AppSourceTree.Xaml+"Key") == "SaSidebarMaterialBrush"),"Color"));
    }

    [Fact]
    public void History_HeadingColumn_IsIndependentOfBreadcrumbOrigin()
    {
        var d = AppSourceTree.LoadXaml("Views/HistoryPage.xaml");
        var heading = d.Descendants().Single(e => A(e,"AutomationProperties.HeadingLevel") == "Level1");
        var header = heading.Parent!.Parent!;
        Assert.Equal("StackPanel", header.Name.LocalName);
        Assert.Null(A(heading.Parent,"Grid.Column"));
        var breadcrumb = header.Elements().Single(e => e.Name.LocalName == "Button");
        Assert.Equal("22", A(breadcrumb,"Height"));
        Assert.StartsWith("{Binding ShowDetailBack,", A(breadcrumb,"Visibility"));
        Assert.Equal("{Binding BackAutomationName}", A(breadcrumb,"AutomationProperties.Name"));
        // In either origin a vertical sibling contributes height only; no horizontal column or margin precedes H1.
        Assert.Null(A(header,"Margin"));
        Assert.Null(A(heading.Parent,"Margin"));
        Assert.Equal("{StaticResource SaPagePadding}", A(header.Parent!, "Padding"));
        Assert.Equal("{StaticResource SaPagePaddingCompact}", Named(d,"Narrow").Descendants().Single(e => A(e,"Target") == "PageRoot.Padding").Attribute("Value")!.Value);
    }

    [Theory]
    [InlineData("en-US", "Back to cache-01")]
    [InlineData("pt-PT", "Voltar a cache-01")]
    [InlineData("pt-BR", "Voltar para cache-01")]
    public void History_BackNameIncludesVisibleServer_InEveryCulture(string culture, string expected)
    {
        var d = XDocument.Load(AppSourceTree.Full($"Resources/{culture}/Resources.resw"));
        var format = d.Root!.Elements("data").Single(e => A(e,"name") == "HistoryBackToServerFormat").Element("value")!.Value;
        Assert.Equal(expected, string.Format(CultureInfo.InvariantCulture, format,"cache-01"));
        Assert.Contains("HistoryBackToServerFormat", AppSourceTree.CodeWithoutComments("ViewModels/HistoryViewModel.cs"));
    }

    [Theory]
    [InlineData(464,0)] [InlineData(640,0)] [InlineData(699,0)] [InlineData(700,620)] [InlineData(900,620)]
    public void EmptyBlocks_CentreOnlyWhenUsableHeightIsSufficient(double height, double expected) =>
        Assert.Equal(expected, DashboardPage.EmptyStateMinimumHeight(height));

    /// <summary>
    /// UI.7 B-5: the onboarding focus preparation existed only so a MODAL editor captured the Visão geral H1 as its return
    /// origin. The editor is a page now (its origin is the Visão geral, focus returns through ServerEditorReturnFocus), so
    /// the hook is retired with its contract - and must not come back half-wired.
    /// </summary>
    [Fact]
    public void OnboardingEditorPreparation_IsRetiredWithTheModal()
    {
        var code = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        Assert.DoesNotContain("PreparingEditor", code, StringComparison.Ordinal);
        Assert.DoesNotContain("PrepareOnboardingEditorAsync", code, StringComparison.Ordinal);
        Assert.DoesNotContain("PreparingEditor", AppSourceTree.CodeWithoutComments("ViewModels/OnboardingViewModel.cs"), StringComparison.Ordinal);
        Assert.Contains("FirstServerStateBlock.MinHeight = HiddenStateBlock.MinHeight = EmptyStateMinimumHeight(usable)",AppSourceTree.CodeWithoutComments("Views/DashboardPage.xaml.cs"));
    }
}
