namespace ServerMonitor.App.Shell.Tray;

/// <summary>
/// What the user chose from the tray menu. The set is product-fixed; <see cref="TrayFlyoutMenu"/>
/// fixes the ORDER and the resource keys separately.
/// </summary>
internal enum TrayCommand
{
    Open,
    ToggleCompact,
    RefreshAll,
    Settings,
    Exit
}
