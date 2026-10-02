using System.Globalization;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// D-UI3-4 revised (Prism R1 F6): X marks are ROUND local times drawn at their REAL position - never the text of a rounded
/// time on an exact quartile (that lies about the position and changes every minute).
/// </summary>
public sealed class HistoryRoundTicksTests
{
    private static readonly CultureInfo Pt = HistoryPresentation.FormatCulture("pt-PT");

    private static IReadOnlyList<HistoryAxisTick> Ticks(DateTimeOffset end, HistoryTimeRange range, int max = 5, TimeZoneInfo? tz = null) =>
        HistoryPresentation.RoundTicks(end - range.ToDuration(), end, range, max, tz ?? TimeZoneInfo.Utc, Pt, "HH:mm", "d MMM");

    [Theory]
    [InlineData(HistoryTimeRange.LastHour, 15)]
    [InlineData(HistoryTimeRange.Last6Hours, 90)]
    [InlineData(HistoryTimeRange.Last24Hours, 360)]
    [InlineData(HistoryTimeRange.Last7Days, 1440)]
    [InlineData(HistoryTimeRange.Last30Days, 10080)]
    public void EveryMarkIsARoundBoundary_AtItsExactPosition(HistoryTimeRange range, int baseMinutes)
    {
        var end = new DateTimeOffset(2026, 9, 29, 14, 59, 0, TimeSpan.Zero);   // "now" is never round
        var start = end - range.ToDuration();
        var ticks = Ticks(end, range);

        Assert.InRange(ticks.Count, 2, 5);
        foreach (var tick in ticks)
        {
            Assert.InRange(tick.Fraction, 0, 1);
            var at = start + TimeSpan.FromTicks((long)Math.Round((end - start).Ticks * tick.Fraction));
            var minutesSinceMidnight = (int)at.TimeOfDay.TotalMinutes;
            Assert.Equal(0, at.Second);
            Assert.True(baseMinutes >= 1440 ? minutesSinceMidnight == 0 : minutesSinceMidnight % baseMinutes == 0,
                $"{range}: {at:O} is not on a round boundary");
            var expectedLabel = at.ToString(HistoryPresentation.UsesTimeOfDayAxis(range) ? "HH:mm" : "d MMM", Pt);
            Assert.Equal(expectedLabel, tick.Label);   // the text is the time AT that position
        }

        Assert.True(ticks.Select(t => t.Fraction).SequenceEqual(ticks.Select(t => t.Fraction).Order()));
    }

    [Fact]
    public void Weeks_StartOnMonday()
    {
        var ticks = Ticks(new DateTimeOffset(2026, 9, 29, 15, 0, 0, TimeSpan.Zero), HistoryTimeRange.Last30Days);

        Assert.Equal(new[] { "31 ago", "7 set", "14 set", "21 set", "28 set" }, ticks.Select(t => t.Label));   // Mondays
    }

    [Fact]
    public void NarrowLayouts_GetAtMostThreeMarks()
    {
        foreach (var range in Enum.GetValues<HistoryTimeRange>())
        {
            Assert.InRange(Ticks(new DateTimeOffset(2026, 9, 29, 14, 59, 0, TimeSpan.Zero), range, max: 3).Count, 1, 3);
        }
    }

    [Fact]
    public void LocalTimeZone_DecidesTheBoundaries()
    {
        var plusOne = TimeZoneInfo.CreateCustomTimeZone("QA+1", TimeSpan.FromHours(1), "QA+1", "QA+1");

        var ticks = Ticks(new DateTimeOffset(2026, 9, 29, 15, 0, 0, TimeSpan.Zero), HistoryTimeRange.Last24Hours, tz: plusOne);

        // Local 16:00 .. 16:00: the 6 h boundaries are 18:00, 00:00, 06:00, 12:00 LOCAL.
        Assert.Equal(new[] { "18:00", "00:00", "06:00", "12:00" }, ticks.Select(t => t.Label));
        Assert.Equal(2 / 24d, ticks[0].Fraction, 9);
    }

    [Fact]
    public void EmptyOrInvertedRange_HasNoMarks()
    {
        var now = new DateTimeOffset(2026, 9, 29, 15, 0, 0, TimeSpan.Zero);

        Assert.Empty(HistoryPresentation.RoundTicks(now, now, HistoryTimeRange.LastHour, 5, TimeZoneInfo.Utc, Pt, "HH:mm", "d MMM"));
        Assert.Empty(HistoryPresentation.RoundTicks(now, now.AddHours(-1), HistoryTimeRange.LastHour, 5, TimeZoneInfo.Utc, Pt, "HH:mm", "d MMM"));
    }
}
