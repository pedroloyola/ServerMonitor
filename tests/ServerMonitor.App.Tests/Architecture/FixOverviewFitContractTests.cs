using System.Globalization;
using System.Xml.Linq;
using ServerMonitor.App.Converters;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// FIX-OVERVIEW-FIT. D-1: the Overview health bar (213, Figma 112:1018) is never clipped. The cards sit side by side
/// only from a viewport derived from the bar, the gaps and the worst count; the spacer minimum is 24; and when a
/// measured row still cannot hold the line, the bar moves under the count. D-2: on the pages that scroll as a whole,
/// the scroll viewport starts below the native caption band (32 = ShellDragRegion), and their top padding is 8, so at
/// rest the header stays at y40 and scrolled content never draws under the caption buttons or the drag strip.
/// </summary>
public sealed class FixOverviewFitContractTests
{
    private const string Overview = "Views/DashboardPage.xaml";

    private static string? A(XElement e, string name) => (string?)e.Attribute(name);

    private static string? Name(XElement e) => (string?)e.Attribute(AppSourceTree.Xaml + "Name");

    private static XElement Key(XDocument doc, string key) =>
        doc.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == key);

    private static double[] Thickness(string key)
    {
        var parts = Key(AppSourceTree.LoadXaml("Styles/Tokens/Spacing.xaml"), key).Value.Split(',')
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        return parts.Length == 1 ? [parts[0], parts[0], parts[0], parts[0]] : parts;
    }

    // ---------------------------------------------------------------- D-1 rule B: the pure placement rule

    [Theory]
    [InlineData(500, 62, 163, false)] // worst 3-digit count in a 1000 viewport: fits
    [InlineData(485, 62, 163, true)]  // one short of the line: the bar goes under the count
    [InlineData(486, 62, 163, false)] // exactly the line: fits (inclusive boundary)
    [InlineData(439, 16, 150, false)] // today's "8 de 16" in a 900 viewport row: fits with the 24 spacer
    [InlineData(500, 82, 175, true)]  // 4 digits: under the count
    [InlineData(0, 0, 0, true)]       // a row with no width yet cannot hold the bar
    public void TheBarMovesUnderTheCount_OnlyWhenTheLineDoesNotFit(double row, double count, double total, bool below)
    {
        Assert.Equal(below, OverviewLayout.HealthBarBelowCount(row, count, total));
    }

    [Fact]
    public void ThePlacementRule_CountsEveryPartOfTheLine()
    {
        // 8 + 8 + 24 + 8 + 213 around the two texts: the boundary moves by exactly one DIP per DIP of text.
        var line = OverviewLayout.HealthRowGap * 3 + OverviewLayout.HealthSpacerMinWidth + OverviewPresentation.HealthBarWidth;
        Assert.Equal(261, line);
        Assert.False(OverviewLayout.HealthBarBelowCount(line + 30, 10, 20));
        Assert.True(OverviewLayout.HealthBarBelowCount(line + 29.9, 10, 20));
    }

    // ---------------------------------------------------------------- D-1 rule A: the breakpoint is derived, not chosen

    [Fact]
    public void TheTwoColumnBreakpoint_HoldsTheWorstCount_AndIsTheOneTheXamlUses()
    {
        var needed = OverviewLayout.MinTwoColumnViewport(OverviewLayout.HealthCountBudget);
        Assert.Equal(233, OverviewLayout.HealthCountBudget);
        Assert.InRange(needed, 900, OverviewLayout.TwoColumnViewport); // 976.7: a 1000 breakpoint leaves 23 of slack
        Assert.Equal(1000, OverviewLayout.TwoColumnViewport);

        var states = AppSourceTree.LoadXaml(Overview).Descendants().Where(e => e.Name.LocalName == "VisualState").ToList();
        var wide = states.Single(s => Name(s) == "Wide").Descendants().Single(e => e.Name.LocalName == "SaContentWidthTrigger");
        var medium = states.Single(s => Name(s) == "Medium").Descendants().Single(e => e.Name.LocalName == "SaContentWidthTrigger");
        var breakpoint = OverviewLayout.TwoColumnViewport.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(breakpoint, A(wide, "MinWidth"));
        Assert.Equal(breakpoint, A(medium, "MaxWidth"));
    }

    [Fact]
    public void TheDerivation_UsesTheLayoutsRealNumbers()
    {
        var spacing = AppSourceTree.LoadXaml("Styles/Tokens/Spacing.xaml");
        double Token(string key) => double.Parse(Key(spacing, key).Value, CultureInfo.InvariantCulture);
        Assert.Equal(Token("SaSpace8"), OverviewLayout.HealthRowGap);
        Assert.Equal(Token("SaSpace24"), OverviewLayout.HealthSpacerMinWidth);
        Assert.Equal(Token("SaSpace24"), OverviewLayout.HealthRowColumnGap);
        var scroll = Thickness("SaScrollPagePadding");
        Assert.Equal(scroll[0] + scroll[2], OverviewLayout.PagePaddingSides);

        var row = AppSourceTree.LoadXaml(Overview).Descendants().Single(e => Name(e) == "HealthRow");
        var columns = row.Elements().Single(e => e.Name.LocalName == "Grid.ColumnDefinitions").Elements().Select(c => A(c, "Width")).ToList();
        Assert.Equal(["624*", "392*"], columns);
        Assert.Equal(624.0 / 1016, OverviewLayout.HealthColumnShare, 6);
        Assert.Equal("{StaticResource SaSpace24}", A(row, "ColumnSpacing"));
        Assert.Equal("{StaticResource SaSpace8}", A(AppSourceTree.LoadXaml(Overview).Descendants().Single(e => Name(e) == "HealthCountRow"), "ColumnSpacing"));
    }

    [Fact]
    public void TheHealthRow_HasASpacerOf24_AndNothingInItHasAFixedWidth()
    {
        var elements = AppSourceTree.LoadXaml(Overview).Descendants().ToList();
        var row = elements.Single(e => Name(e) == "HealthCountRow");
        Assert.Equal("{StaticResource SaSpace24}", A(row.Descendants().Single(c => Name(c) == "HealthSpacerColumn"), "MinWidth"));

        var bar = elements.Single(e => Name(e) == "HealthSegments");
        // D-5: the bar's 213 is a minimum, never a fixed width (a fixed one clips what layout rounding adds above 100 %).
        Assert.Equal(OverviewPresentation.HealthBarWidth.ToString(CultureInfo.InvariantCulture), A(bar, "MinWidth"));
        Assert.All(row.Elements().Where(e => !e.Name.LocalName.Contains('.')), child => Assert.Null(A(child, "Width")));
    }

    [Fact]
    public void ThePage_AppliesThePlacementRule_FromTheMeasuredRowAndTexts()
    {
        var code = AppSourceTree.CodeWithoutComments("Views/DashboardPage.xaml.cs");
        Assert.Contains("OverviewLayout.HealthBarBelowCount(", code, StringComparison.Ordinal);
        Assert.Contains("HealthCountRow.ActualWidth, HealthCountText.DesiredSize.Width, HealthTotalText.DesiredSize.Width", code, StringComparison.Ordinal);
        foreach (var source in new[] { "HealthCountRow.SizeChanged", "HealthCountText.SizeChanged", "HealthTotalText.SizeChanged", "WidthStates.CurrentStateChanged" })
        {
            Assert.Contains(source + " += (_, _) => UpdateHealthBarPlacement();", code, StringComparison.Ordinal);
        }

        // Below: the NarrowWide arrangement exactly (row 1, column 0, span 4, row gap 12).
        var narrowWide = AppSourceTree.LoadXaml(Overview).Descendants().Single(e => e.Name.LocalName == "VisualState" && Name(e) == "NarrowWide");
        string Setter(string target) => A(narrowWide.Descendants().Single(e => A(e, "Target") == target), "Value")!;
        Assert.Equal("1", Setter("HealthSegments.(Grid.Row)"));
        Assert.Equal("0", Setter("HealthSegments.(Grid.Column)"));
        Assert.Equal("4", Setter("HealthSegments.(Grid.ColumnSpan)"));
        Assert.Equal("{StaticResource SaSpace12}", Setter("HealthCountRow.RowSpacing"));
        Assert.Contains("Grid.SetRow(HealthSegments, 1);", code, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(HealthSegments, 0);", code, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumnSpan(HealthSegments, 4);", code, StringComparison.Ordinal);
        Assert.Contains("HealthCountRow.RowSpacing = (double)Application.Current.Resources[\"SaSpace12\"];", code, StringComparison.Ordinal);
        // and back: the XAML's own placement (row 0, column 3, span 1, no row gap).
        Assert.Contains("Grid.SetRow(HealthSegments, 0);", code, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumn(HealthSegments, 3);", code, StringComparison.Ordinal);
        Assert.Contains("Grid.SetColumnSpan(HealthSegments, 1);", code, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- D-2: scroll viewports start below the caption band

    /// <summary>Every view whose page root sits directly in a ScrollViewer that scrolls vertically (found, not listed).</summary>
    public static TheoryData<string> ScrollingPages() => new(FindScrollingPages());

    private static List<string> FindScrollingPages()
    {
        var data = new List<string>();
        foreach (var file in Directory.GetFiles(AppSourceTree.Full("Views"), "*.xaml").Order(StringComparer.Ordinal))
        {
            var root = XDocument.Load(file).Descendants().SingleOrDefault(e => Name(e) == "PageRoot");
            if (root?.Parent is { } parent && parent.Name.LocalName == "ScrollViewer" && A(parent, "VerticalScrollMode") != "Disabled")
            {
                data.Add("Views/" + Path.GetFileName(file));
            }
        }

        return data;
    }

    [Fact]
    public void TheScrollingPages_AreTheSixThatScrollAsAWhole()
    {
        Assert.Equal(
            ["Views/DashboardPage.xaml", "Views/ServerDetailPage.xaml", "Views/ServerEditorPage.xaml", "Views/ServersPage.xaml", "Views/SettingsDataPage.xaml", "Views/SettingsPage.xaml"],
            FindScrollingPages());
    }

    [Theory]
    [MemberData(nameof(ScrollingPages))]
    public void AScrollingPage_StartsItsViewportBelowTheCaptionBand(string page)
    {
        var doc = AppSourceTree.LoadXaml(page);
        var root = doc.Descendants().Single(e => Name(e) == "PageRoot");
        Assert.Equal("{StaticResource SaPageScrollInset}", A(root.Parent!, "Margin"));
        Assert.Equal("{StaticResource SaScrollPagePadding}", A(root, "Padding"));
        var setters = doc.Descendants().Where(e => e.Name.LocalName == "Setter" && A(e, "Target") == "PageRoot.Padding").ToList();
        Assert.NotEmpty(setters);
        Assert.All(setters, s => Assert.Equal("{StaticResource SaScrollPagePaddingCompact}", A(s, "Value")));
    }

    [Fact]
    public void TheNonScrollingPages_KeepTheFullPadding()
    {
        foreach (var page in new[] { "Views/HistoryPage.xaml", "Views/WorkloadsPage.xaml" })
        {
            var doc = AppSourceTree.LoadXaml(page);
            Assert.Equal("{StaticResource SaPagePadding}", A(doc.Descendants().Single(e => Name(e) == "PageRoot"), "Padding"));
            Assert.DoesNotContain(doc.Descendants(), e => A(e, "Margin") == "{StaticResource SaPageScrollInset}");
        }
    }

    [Fact]
    public void TheBandPlusTheTopPadding_IsThePagesTop40_AndTheBandIsTheDragStrip()
    {
        var spacing = AppSourceTree.LoadXaml("Styles/Tokens/Spacing.xaml");
        var band = double.Parse(Key(spacing, "SaCaptionBandHeight").Value, CultureInfo.InvariantCulture);
        var drag = AppSourceTree.LoadXaml("MainWindow.xaml").Descendants().Single(e => Name(e) == "ShellDragRegion");
        Assert.Equal(band, double.Parse(A(drag, "Height")!, CultureInfo.InvariantCulture));
        Assert.Equal(32, band);

        Assert.Equal([0, band, 0, 0], Thickness("SaPageScrollInset"));
        foreach (var (scroll, full) in new[] { ("SaScrollPagePadding", "SaPagePadding"), ("SaScrollPagePaddingCompact", "SaPagePaddingCompact") })
        {
            var s = Thickness(scroll);
            var f = Thickness(full);
            Assert.Equal(40, s[1] + band);
            Assert.Equal(f[1], s[1] + band);
            Assert.Equal(f[0], s[0]);
            Assert.Equal(f[2], s[2]);
            Assert.Equal(f[3], s[3]);
        }
    }

    [Fact]
    public void TheOverviewEmptyStateHeight_IsMeasuredInsideTheScrollViewport()
    {
        // The 32 band is outside the scroll viewport now: the usable height is the viewport's, not the page's.
        Assert.Contains("var usable = PageScroll.ActualHeight - PageRoot.Padding.Top", AppSourceTree.CodeWithoutComments("Views/DashboardPage.xaml.cs"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- D-3: Settings link cards stack before they clip

    [Theory]
    [InlineData(1, 804, 121, 3)]   // window 900 with the rail: 124.3 of room
    [InlineData(1, 704, 121, 1)]   // window 800: 91 of room, stacked
    [InlineData(1, 800, 123.0, 3)] // room exactly 123.0: fits (inclusive)
    [InlineData(1, 799, 123, 1)]
    [InlineData(0, 700, 121, 2)]   // the public cards alone: two columns from the Narrow trigger up
    [InlineData(0, 699, 121, 1)]
    [InlineData(1, 840, 136, 3)]   // window 1080 with the sidebar
    [InlineData(0, 700, 197, 2)]   // two columns at 700: 197 of room
    [InlineData(0, 700, 197.5, 1)]
    public void TheCards_StackWhenTheWidestTextDoesNotFitItsColumn(int contributed, double viewport, double widest, int columns)
    {
        Assert.Equal(columns, SettingsLinkCardLayout.Columns(contributed, viewport, widest));
    }

    [Fact]
    public void TheCardRoom_IsTheMeasuredColumnOfTheLiveQa()
    {
        // Windows 900 / 960 / 1020 (rail: window - 16 - 80) and 1080 / 1140 (sidebar: window - 16 - 224), three cards:
        // the live text column was 125 / 145 / 165 / 137 / 157.
        double Room(double viewport) => SettingsLinkCardLayout.TextRoom(SettingsLinkCardLayout.WideColumnsWithCards, viewport);
        Assert.Equal(124.3, Room(900 - 96), 1);
        Assert.Equal(144.3, Room(960 - 96), 1);
        Assert.Equal(164.3, Room(1020 - 96), 1);
        Assert.Equal(136.3, Room(1080 - 240), 1);
        Assert.Equal(156.3, Room(1140 - 240), 1);
    }

    [Fact]
    public void TheCardArithmetic_IsTheXamls()
    {
        var page = AppSourceTree.LoadXaml("Views/SettingsPage.xaml");
        var spacing = AppSourceTree.LoadXaml("Styles/Tokens/Spacing.xaml");
        double Token(string key) => double.Parse(Key(spacing, key).Value, CultureInfo.InvariantCulture);
        var style = Key(AppSourceTree.LoadXaml("Styles/Components/Sa.Buttons.xaml"), "SaDisclosureButtonStyle");
        string Setter(string property) => A(style.Elements().Single(e => e.Name.LocalName == "Setter" && A(e, "Property") == property), "Value")!;
        var padding = Setter("Padding").Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal("{StaticResource SaBorderThickness}", Setter("BorderThickness"));
        var border = double.Parse(Key(AppSourceTree.LoadXaml("Styles/Tokens/Borders.xaml"), "SaBorderThickness").Value, CultureInfo.InvariantCulture);

        // Every card (the two built-in ones and the template) is icon 18 · 12 · text · 12 · arrow 17.
        var cards = page.Descendants().Where(e => Name(e) == "DisclosureGrid").Single().Elements().Where(e => e.Name.LocalName == "Button")
            .Select(b => b.Elements().Single())
            .Append(page.Descendants().Single(e => e.Name.LocalName == "DataTemplate" && (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "SettingsLinkCardTemplate").Elements().Single())
            .ToList();
        Assert.Equal(3, cards.Count);
        foreach (var grid in cards)
        {
            Assert.Equal("{StaticResource SaSpace12}", A(grid, "ColumnSpacing"));
            var icons = grid.Elements().Where(e => e.Name.LocalName == "SaIcon").Select(i => A(i, "Size")).ToList();
            Assert.Equal(["18", "17"], icons);
        }

        Assert.Equal(padding[0] + padding[2] + (2 * border) + 18 + (2 * Token("SaSpace12")) + 17, SettingsLinkCardLayout.CardChrome);
        Assert.Equal("{StaticResource SaSpace24}", A(page.Descendants().Single(e => Name(e) == "DisclosureGrid"), "ColumnSpacing"));
        Assert.Equal(Token("SaSpace24"), SettingsLinkCardLayout.CardGap);
        var scroll = Thickness("SaScrollPagePadding");
        Assert.Equal(scroll[0] + scroll[2], SettingsLinkCardLayout.PageSides);
        var narrow = page.Descendants().Single(e => e.Name.LocalName == "VisualState" && Name(e) == "Narrow")
            .Descendants().Single(e => e.Name.LocalName == "SaContentWidthTrigger");
        Assert.Equal(SettingsLinkCardLayout.NarrowViewport.ToString(CultureInfo.InvariantCulture), A(narrow, "MaxWidth"));
    }

    [Fact]
    public void TheSettingsPage_MeasuresEveryCardText_AndReplacesTheCardsWhenAnyOfItChanges()
    {
        var code = AppSourceTree.CodeWithoutComments("Views/SettingsPage.xaml.cs");
        Assert.Contains("var widest = Math.Max(UnconstrainedWidth(DataDisclosureText), UnconstrainedWidth(AboutDisclosureText));", code, StringComparison.Ordinal);
        Assert.Contains("card.ContentTemplateRoot is Panel root && root.Children.OfType<TextBlock>().FirstOrDefault() is { } text", code, StringComparison.Ordinal);
        // Each contributed card's text counts towards the widest, not only the two built-in cards.
        var widestBody = code[code.IndexOf("private double WidestCardText()", StringComparison.Ordinal)..];
        widestBody = widestBody[..widestBody.IndexOf("return widest;", StringComparison.Ordinal)];
        Assert.Contains("foreach (var (_, card, _) in _linkCards)", widestBody, StringComparison.Ordinal);
        Assert.Contains("widest = Math.Max(widest, UnconstrainedWidth(text));", widestBody, StringComparison.Ordinal);
        Assert.Contains("text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));", code, StringComparison.Ordinal);
        foreach (var source in new[] { "SizeChanged", "DataDisclosureText.SizeChanged", "AboutDisclosureText.SizeChanged", "card.SizeChanged" })
        {
            Assert.Contains(source + " += (_, _) => PlaceLinkCards();", code, StringComparison.Ordinal);
        }

        // A label or hint change re-places the cards.
        var apply = code[code.IndexOf("private void ApplyLinkCardText()", StringComparison.Ordinal)..];
        apply = apply[..apply.IndexOf("private ", 10, StringComparison.Ordinal)];
        Assert.Contains("PlaceLinkCards();", apply, StringComparison.Ordinal);
        // One per row is the Narrow state's arrangement: second column 0, no column gap, the 12 row gap.
        var page = AppSourceTree.LoadXaml("Views/SettingsPage.xaml");
        var setters = page.Descendants().Single(e => e.Name.LocalName == "VisualState" && Name(e) == "Narrow").Descendants()
            .Where(e => e.Name.LocalName == "Setter").ToDictionary(e => A(e, "Target")!, e => A(e, "Value"));
        Assert.Equal("0", setters["DisclosureColumn2.Width"]);
        Assert.Equal("0", setters["DisclosureGrid.ColumnSpacing"]);
        Assert.Equal("{StaticResource SaSpace12}", setters["DisclosureGrid.RowSpacing"]);
        Assert.Contains("DisclosureColumn2.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);", code, StringComparison.Ordinal);
        Assert.Contains("DisclosureGrid.ColumnSpacing = stacked ? 0 : (double)Application.Current.Resources[\"SaSpace24\"];", code, StringComparison.Ordinal);
        Assert.Contains("? (double)Application.Current.Resources[\"SaSpace12\"]", code, StringComparison.Ordinal);
    }
}
