namespace ServerMonitor.Infrastructure.Persistence;

/// <summary>
/// Where the widget state snapshot is written. LOCAL-FIRST, same folder as history
/// (<c>%LOCALAPPDATA%\ServerMonitor\widget-state.json</c>) — never OneDrive, a roaming profile, or a
/// network share (ADR-018 §7). The internal folder name stays <c>ServerMonitor</c> for compatibility;
/// it is deliberately NOT renamed to ServerAlyzer in M13 (§7).
/// </summary>
public sealed record WidgetStateOptions
{
    public required string SnapshotPath { get; init; }

    public static WidgetStateOptions ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        return new WidgetStateOptions
        {
            SnapshotPath = Path.Combine(localApplicationData, "ServerMonitor", "widget-state.json")
        };
    }
}
