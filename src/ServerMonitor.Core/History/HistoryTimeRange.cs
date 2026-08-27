namespace ServerMonitor.Core.History;

/// <summary>The fixed history windows offered by the UI (spec §35). No custom date picker in M10.</summary>
public enum HistoryTimeRange
{
    LastHour,
    Last6Hours,
    Last24Hours,
    Last7Days,
    Last30Days
}

public static class HistoryTimeRangeExtensions
{
    public static TimeSpan ToDuration(this HistoryTimeRange range) => range switch
    {
        HistoryTimeRange.LastHour => TimeSpan.FromHours(1),
        HistoryTimeRange.Last6Hours => TimeSpan.FromHours(6),
        HistoryTimeRange.Last24Hours => TimeSpan.FromHours(24),
        HistoryTimeRange.Last7Days => TimeSpan.FromDays(7),
        HistoryTimeRange.Last30Days => TimeSpan.FromDays(30),
        _ => throw new ArgumentOutOfRangeException(nameof(range))
    };
}
