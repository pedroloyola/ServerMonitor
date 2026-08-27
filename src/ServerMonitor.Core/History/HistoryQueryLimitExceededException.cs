namespace ServerMonitor.Core.History;

/// <summary>
/// Raised when a history database contains more raw rows for one query than can be materialized
/// safely. The UI treats this as history unavailable rather than displaying a misleading prefix.
/// </summary>
public sealed class HistoryQueryLimitExceededException(int maximumRows)
    : Exception($"History query exceeded the defensive limit of {maximumRows} rows.")
{
    public int MaximumRows { get; } = maximumRows;
}
