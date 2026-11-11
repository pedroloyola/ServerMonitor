using System.Globalization;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Monitoring;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// FIX-OVERVIEW-FIT D-5: the health bar's last segment lost its rounded right end. Fractional widths summed to exactly 213
/// (16 servers: 78.5 + 4 × 28.63 + 4 gaps of 5), layout rounding snapped each segment to whole pixels (79 + 4 × 29 + 20 =
/// 215) and the fixed-width bar clipped the last 2 px. The widths are now whole pixels that never exceed the bar, and the
/// bar has a minimum width instead of a fixed one, so a pixel added by rounding at 125-175 % is never clipped.
/// </summary>
public sealed class HealthBarWholePixelTests
{
    private const int Bar = (int)OverviewPresentation.HealthBarWidth;
    private const int Gap = (int)OverviewPresentation.HealthBarGap;
    private const int Min = (int)OverviewPresentation.HealthSegmentMinWidth;
    private const int Max = (int)OverviewPresentation.HealthSegmentMaxWidth;

    private static HealthSummary Summary(int healthy, int warning = 0, int critical = 0, int offline = 0, int unknown = 0) =>
        HealthSummary.From(Enumerable.Repeat(ServerHealth.Healthy, healthy)
            .Concat(Enumerable.Repeat(ServerHealth.Warning, warning))
            .Concat(Enumerable.Repeat(ServerHealth.Critical, critical))
            .Concat(Enumerable.Repeat(ServerHealth.Offline, offline))
            .Concat(Enumerable.Repeat(ServerHealth.Unknown, unknown)));

    private static double Extent(IReadOnlyList<HealthBarSegment> segments) =>
        segments.Sum(segment => segment.Width) + (Gap * (segments.Count - 1));

    /// <summary>The fleet the owner saw it on (--qa-health: 8 healthy, 2 of each other state): whole pixels, exactly 213.</summary>
    [Fact]
    public void TheSixteenServerFleet_GetsWholePixels_ThatFillTheBarExactly()
    {
        var segments = OverviewPresentation.BarSegments(Summary(8, 2, 2, 2, 2));

        Assert.Equal(5, segments.Count);
        Assert.All(segments, segment => Assert.Equal(Math.Floor(segment.Width), segment.Width));
        Assert.Equal(Bar, Extent(segments));
        Assert.Equal([78, 29, 29, 29, 28], segments.Select(segment => (int)segment.Width));
    }

    private static IEnumerable<(int Healthy, int Warning, int Critical, int Offline, int Unknown)> Mixes()
    {
        int[] counts = [0, 1, 2, 3, 7, 13, 50, 101, 499];
        foreach (var h in counts)
        foreach (var w in counts)
        foreach (var c in counts)
        foreach (var o in new[] { 0, 1, 2, 13 })
        foreach (var u in new[] { 0, 2, 50 })
        {
            if (h + w + c + o + u > 0)
            {
                yield return (h, w, c, o, u);
            }
        }

        // Every discrete fleet size, in three state mixes.
        for (var n = 1; n <= OverviewPresentation.MaxDiscreteHealthSegments; n++)
        {
            yield return (n, 0, 0, 0, 0);
            yield return (0, 0, 0, 0, n);
            yield return (n / 2, n - (n / 2), 0, 0, 0);
        }
    }

    /// <summary>
    /// Every fleet (8,000+ mixes, one test): whole pixels, never below the minimum, never more than the bar. Aggregated
    /// fleets and discrete ones of 7 or more fill the bar exactly; discrete segments are at most 30, equal to within 1 px,
    /// the wider ones first.
    /// </summary>
    [Fact]
    public void EveryFleet_GetsWholePixels_WithinTheBar_AndNoneBelowTheMinimum()
    {
        var failures = new List<string>();
        var checkedMixes = 0;
        foreach (var (healthy, warning, critical, offline, unknown) in Mixes())
        {
            checkedMixes++;
            var total = healthy + warning + critical + offline + unknown;
            var segments = OverviewPresentation.BarSegments(Summary(healthy, warning, critical, offline, unknown));
            var widths = segments.Select(segment => segment.Width).ToList();
            var discrete = total <= OverviewPresentation.MaxDiscreteHealthSegments;
            var expectedExtent = !discrete || total >= 7 ? Bar : (Max * total) + (Gap * (total - 1));
            if (widths.Any(width => width != Math.Floor(width))
                || widths.Any(width => width < Min)
                || Extent(segments) != expectedExtent
                || (discrete && (segments.Count != total || widths.Any(width => width > Max) || widths.Max() - widths.Min() > 1
                    || !widths.SequenceEqual(widths.OrderByDescending(width => width)))))
            {
                failures.Add($"{healthy}/{warning}/{critical}/{offline}/{unknown}: {string.Join(",", widths)}");
            }
        }

        Assert.True(checkedMixes > 8000, $"{checkedMixes}");
        Assert.True(failures.Count == 0, $"{failures.Count} fleets: " + string.Join(" | ", failures.Take(10)));
    }

    /// <summary>The allocation itself: the largest remainders get the leftover pixels, ties in bar order.</summary>
    [Fact]
    public void TheLeftoverPixels_GoToTheLargestRemainders()
    {
        // 193 to share, 60 reserved, 133 spare by 8:2:2:2:2 -> 66.5 and 16.625 x 4: floors 78 + 28 x 4 = 190, 3 left over;
        // the remainders .625 beat .5, so the first three of the four small states get them.
        Assert.Equal([78, 29, 29, 29, 28], OverviewPresentation.SegmentWidths([8, 2, 2, 2, 2], discrete: false));
        // 198 to share, 48 reserved, 150 spare by 2:1:1:5 (total 9) -> 33.33, 16.67, 16.67, 83.33: floors 45 + 28 + 28 + 95 = 196,
        // 2 left over; remainders .33, .67, .67, .33 -> the two .67 get them.
        Assert.Equal([45, 29, 29, 95], OverviewPresentation.SegmentWidths([2, 1, 1, 5], discrete: false));
        // Equal remainders: bar order decides (150 by 1:1:1:5 -> 18.75 x 3 + 93.75, floors 195, 3 left over).
        Assert.Equal([31, 31, 31, 105], OverviewPresentation.SegmentWidths([1, 1, 1, 5], discrete: false));
        // 12 discrete: 158 = 12 x 13 + 2.
        Assert.Equal([14, 14, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13], OverviewPresentation.SegmentWidths(Enumerable.Repeat(1, 12).ToList(), discrete: true));
        Assert.Empty(OverviewPresentation.SegmentWidths([], discrete: false));
    }

    /// <summary>
    /// At 100 % whole-DIP widths render exactly. Above it layout rounding works in physical pixels and can add a pixel or
    /// two, so the bar has a MINIMUM width of 213 and no fixed Width (a fixed one clips what does not fit).
    /// </summary>
    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    public void AtEveryScale_TheBarHasRoomForWhatRoundingAdds(double scale)
    {
        var segments = OverviewPresentation.BarSegments(Summary(8, 2, 2, 2, 2));
        var rendered = segments.Sum(segment => Math.Round(segment.Width * scale, MidpointRounding.AwayFromZero))
            + ((segments.Count - 1) * Math.Round(Gap * scale, MidpointRounding.AwayFromZero));
        var fixedBox = Math.Round(Bar * scale, MidpointRounding.AwayFromZero);
        // The physical extent may exceed a fixed 213-DIP box by a pixel or two: why the bar cannot be fixed-width.
        Assert.InRange(rendered - fixedBox, -3, 3);

        var bar = AppSourceTree.LoadXaml("Views/DashboardPage.xaml").Descendants()
            .Single(e => (string?)e.Attribute(System.Xml.Linq.XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "HealthSegments");
        Assert.Equal(OverviewPresentation.HealthBarWidth.ToString(CultureInfo.InvariantCulture), (string?)bar.Attribute("MinWidth"));
        Assert.Null(bar.Attribute("Width"));
        Assert.Null(bar.Attribute("MaxWidth"));
    }
}
