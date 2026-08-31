using ServerMonitor.WidgetContract;

namespace ServerMonitor.WidgetProvider.Reading;

/// <summary>Outcome of reading the snapshot file.</summary>
public enum WidgetReadStatus
{
    /// <summary>A valid, in-bounds snapshot was read.</summary>
    Available,

    /// <summary>No usable snapshot — the provider should show the neutral "unavailable" state.</summary>
    Unavailable
}

/// <summary>Why a read was unavailable, for neutral logging (never the payload, §31).</summary>
public enum WidgetReadUnavailableReason
{
    None = 0,
    Missing,
    Oversized,
    Corrupt,
    Invalid,
    IoError
}

/// <summary>
/// Result of <see cref="WidgetSnapshotReader.Read"/>. Always well-formed: on any problem the status is
/// <see cref="WidgetReadStatus.Unavailable"/> and <see cref="Snapshot"/> is <c>null</c> — the reader
/// never throws across the provider/COM boundary (§9/§16, L-018).
/// </summary>
public readonly record struct WidgetReadResult(
    WidgetReadStatus Status,
    WidgetStateSnapshot? Snapshot,
    WidgetReadUnavailableReason Reason)
{
    public bool IsAvailable => Status == WidgetReadStatus.Available && Snapshot is not null;

    public static WidgetReadResult Available(WidgetStateSnapshot snapshot) =>
        new(WidgetReadStatus.Available, snapshot, WidgetReadUnavailableReason.None);

    public static WidgetReadResult Unavailable(WidgetReadUnavailableReason reason) =>
        new(WidgetReadStatus.Unavailable, null, reason);
}
