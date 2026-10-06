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
        Assert.Equal("VariableSizedWrapGrid", group.Name.LocalName);
        var width = double.Parse(setters.GetValueOrDefault("RangeItems.ItemWidth", A(group,"ItemWidth")!), CultureInfo.InvariantCulture);
        Assert.True(5 * width + 8 <= contentWidth - 48, "Five periods plus track padding must fit without inflating the selector column.");
        Assert.All(group.Elements(), e => Assert.Equal("0", A(e,"MinWidth")));
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

    [Fact]
    public void OnboardingEditorPreparation_IsWiredToHeadingBeforeCapture()
    {
        var code = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        Assert.Contains("Onboarding.PreparingEditor += PrepareOnboardingEditorAsync", code);
        Assert.Contains("Onboarding.PreparingEditor -= PrepareOnboardingEditorAsync", code);
        var start = code.IndexOf("private Task PrepareOnboardingEditorAsync", StringComparison.Ordinal);
        var end = code.IndexOf("private void OnOnboardingChanged", start, StringComparison.Ordinal);
        var body = code[start..end];
        Assert.Contains("DispatcherQueuePriority.Low", body);
        Assert.True(body.IndexOf("ShellPageFocus.FocusHeading(page, force: true)",StringComparison.Ordinal) < body.IndexOf("ready.SetResult()",StringComparison.Ordinal));
        Assert.Contains("FirstServerStateBlock.MinHeight = HiddenStateBlock.MinHeight = EmptyStateMinimumHeight(usable)",AppSourceTree.CodeWithoutComments("Views/DashboardPage.xaml.cs"));
    }
}
