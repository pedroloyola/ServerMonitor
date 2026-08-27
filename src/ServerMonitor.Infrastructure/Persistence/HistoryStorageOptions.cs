namespace ServerMonitor.Infrastructure.Persistence;

/// <summary>
/// Where and how server history is stored. LOCAL-FIRST: the database lives under
/// <c>%LOCALAPPDATA%\ServerMonitor\history.db</c> — never OneDrive, a roaming profile, or a network
/// share (ADR-015 §5; spec §11). Retention defaults to 30 days.
/// </summary>
public sealed record HistoryStorageOptions
{
    public const int DefaultMaxQueryRows = 100_000;

    public required string DatabasePath { get; init; }

    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Defensive raw-row ceiling before downsampling. The normal 30-day/30-second cadence produces
    /// at most about 86,400 rows; exceeding this indicates an old, altered, or logically corrupt DB.
    /// </summary>
    public int MaxQueryRows { get; init; } = DefaultMaxQueryRows;

    /// <summary>Deterministic integration-test seam invoked only after a real SQLite reader is open.
    /// Production composition leaves this null.</summary>
    internal Action? QueryReaderOpenedForTesting { get; init; }

    public static HistoryStorageOptions ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        return new HistoryStorageOptions
        {
            DatabasePath = Path.Combine(localApplicationData, "ServerMonitor", "history.db")
        };
    }
}
