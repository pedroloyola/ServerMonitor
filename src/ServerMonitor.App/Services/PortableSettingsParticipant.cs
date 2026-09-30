using ServerMonitor.Core.Backup;

namespace ServerMonitor.App.Services;

/// <summary>
/// Backup/restore view of the two portable settings files (M14.6 spec §1, D7): <c>notificationsEnabled</c> and
/// <c>backgroundMonitoringEnabled</c>. Restore replaces each file whole through the service's own lock; the
/// per-install one-shot <c>backgroundNoticeShown</c> flag keeps the TARGET's current value.
/// </summary>
public sealed class PortableSettingsParticipant(
    NotificationSettingsStorageOptions notificationOptions,
    BackgroundSettingsStorageOptions backgroundOptions,
    JsonNotificationSettingsService notifications,
    JsonBackgroundMonitoringSettingsService background) : IPortableSettingsParticipant
{
    public string NotificationSettingsFilePath { get; } = notificationOptions.FilePath;

    public string BackgroundSettingsFilePath { get; } = backgroundOptions.FilePath;

    public PortableSettings ReadCurrent() =>
        new(notifications.NotificationsEnabled, background.BackgroundMonitoringEnabled);

    public byte[] RenderNotificationSettings(PortableSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonNotificationSettingsService.Render(settings.NotificationsEnabled);
    }

    public byte[] RenderBackgroundSettings(PortableSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonBackgroundMonitoringSettingsService.Render(
            settings.BackgroundMonitoringEnabled,
            background.BackgroundNoticeShown);
    }

    public PortableSettings ReadNotificationAndBackground(byte[]? notificationBytes, byte[]? backgroundBytes) =>
        new(
            JsonNotificationSettingsService.Parse(notificationBytes),
            JsonBackgroundMonitoringSettingsService.ParseBackgroundMonitoringEnabled(backgroundBytes));

    public void ReplaceNotificationSettings(RestoreWriteToken token, byte[]? content) =>
        notifications.ReplaceForRestore(token, content);

    public void ReplaceBackgroundSettings(RestoreWriteToken token, byte[]? content) =>
        background.ReplaceForRestore(token, content);
}

/// <summary>Stops the monitoring engine for a restore and restarts it after a rollback (spec §5.2 step 0).</summary>
public sealed class RestoreMonitoringControl(IMonitoringEngine engine) : IRestoreMonitoringControl
{
    public Task StopAsync(CancellationToken cancellationToken) => engine.StopMonitoringAsync(cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) => engine.StartMonitoringAsync(cancellationToken);
}
