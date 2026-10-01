using System.Globalization;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.ViewModels;

/// <summary>
/// Pure, deterministic text helpers for the History screen (UI.3): axis labels (D-UI3-4), the period
/// footer and the culture used to format them. Every format string comes from the caller (resw), so the
/// helper owns only the arithmetic and the time-zone conversion and is unit-testable without any UI.
/// </summary>
public static class HistoryPresentation
{
    /// <summary>The fixed 0–100 % Y axis, top to bottom (the chart scale never auto-zooms, spec §45).</summary>
    public static IReadOnlyList<string> YAxisLabels(CultureInfo culture) =>
    [
        100.ToString(culture),
        50.ToString(culture),
        0.ToString(culture)
    ];

    /// <summary>
    /// The culture dates are formatted in: the explicit UI language when one is set (so the month names
    /// match the resw copy), otherwise the current UI culture. ICU abbreviates pt months with a trailing
    /// dot ("set."); the Figma copy is "28 set 2026", so the clone drops that dot. en-US is unaffected.
    /// </summary>
    public static CultureInfo FormatCulture(string? languageOverride)
    {
        CultureInfo culture;
        try
        {
            culture = string.IsNullOrWhiteSpace(languageOverride)
                ? CultureInfo.CurrentUICulture
                : CultureInfo.GetCultureInfo(languageOverride);
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.CurrentUICulture;
        }

        var clone = (CultureInfo)culture.Clone();
        var format = clone.DateTimeFormat;
        format.AbbreviatedMonthNames = TrimDots(format.AbbreviatedMonthNames);
        format.AbbreviatedMonthGenitiveNames = TrimDots(format.AbbreviatedMonthGenitiveNames);
        return clone;
    }

    /// <summary>
    /// <paramref name="count"/> evenly spaced instants from <paramref name="startUtc"/> to
    /// <paramref name="endUtc"/> inclusive: 5 → the quartiles of the range, 3 → start/middle/end.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> AxisTicks(DateTimeOffset startUtc, DateTimeOffset endUtc, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 2);
        var span = endUtc - startUtc;
        var ticks = new DateTimeOffset[count];
        for (var i = 0; i < count; i++)
        {
            ticks[i] = startUtc + TimeSpan.FromTicks(span.Ticks / (count - 1) * i);
        }

        ticks[count - 1] = endUtc;
        return ticks;
    }

    /// <summary>True when the range is labelled by time of day (1 h/6 h/24 h), false for day labels (7/30 days).</summary>
    public static bool UsesTimeOfDayAxis(HistoryTimeRange range) =>
        range is HistoryTimeRange.LastHour or HistoryTimeRange.Last6Hours or HistoryTimeRange.Last24Hours;

    /// <summary>X-axis labels in local time (D-UI3-4): <paramref name="timeFormat"/> up to 24 h, else <paramref name="dayFormat"/>.</summary>
    public static IReadOnlyList<string> XAxisLabels(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        HistoryTimeRange range,
        int count,
        TimeZoneInfo timeZone,
        CultureInfo culture,
        string timeFormat,
        string dayFormat)
    {
        if (endUtc <= startUtc)
        {
            return [];
        }

        var format = UsesTimeOfDayAxis(range) ? timeFormat : dayFormat;
        return AxisTicks(startUtc, endUtc, count)
            .Select(tick => TimeZoneInfo.ConvertTime(tick, timeZone).ToString(format, culture))
            .ToArray();
    }

    /// <summary>
    /// The footer's date span in local time, e.g. "28–29 set 2026". The four composite formats (args
    /// {0} = start, {1} = end) cover same day, same month, same year and a span across years.
    /// </summary>
    public static string FormatPeriod(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        TimeZoneInfo timeZone,
        CultureInfo culture,
        string sameDayFormat,
        string sameMonthFormat,
        string sameYearFormat,
        string crossYearFormat)
    {
        var start = TimeZoneInfo.ConvertTime(startUtc, timeZone).DateTime;
        var end = TimeZoneInfo.ConvertTime(endUtc, timeZone).DateTime;

        var format = start.Date == end.Date ? sameDayFormat
            : start.Year == end.Year && start.Month == end.Month ? sameMonthFormat
            : start.Year == end.Year ? sameYearFormat
            : crossYearFormat;

        return string.Format(culture, format, start, end);
    }

    private static string[] TrimDots(string[] names) =>
        names.Select(name => name.TrimEnd('.')).ToArray();
}
