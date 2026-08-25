namespace ServerMonitor.App.Services;

/// <summary>Persistent user preference boundary for M8 state notifications.</summary>
public interface INotificationSettingsService
{
    event EventHandler? NotificationsEnabledChanged;

    bool NotificationsEnabled { get; }

    void SetNotificationsEnabled(bool enabled);
}
