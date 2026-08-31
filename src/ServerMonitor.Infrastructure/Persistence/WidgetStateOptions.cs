using ServerMonitor.WidgetContract;

namespace ServerMonitor.Infrastructure.Persistence;

/// <summary>
/// Where the widget state snapshot is written. LOCAL-FIRST, same folder as history
/// (<c>%LOCALAPPDATA%\ServerMonitor\widget-state.json</c>) — never OneDrive, a roaming profile, or a
/// network share (ADR-018 §7). The path is resolved from <see cref="WidgetStateLocation"/>, the single
/// canonical location shared with the out-of-process reader so the two can never drift.
/// </summary>
public sealed record WidgetStateOptions
{
    public required string SnapshotPath { get; init; }

    public static WidgetStateOptions ForCurrentUser() =>
        new() { SnapshotPath = WidgetStateLocation.ForCurrentUser() };
}
