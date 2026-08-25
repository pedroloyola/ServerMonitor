using ServerMonitor.App.Services;

namespace ServerMonitor.App.Qa;

internal sealed class QaNotificationSettingsService : INotificationSettingsService
{
    public event EventHandler? NotificationsEnabledChanged;

    public bool NotificationsEnabled { get; private set; } = true;

    public void SetNotificationsEnabled(bool enabled)
    {
        if (NotificationsEnabled == enabled)
        {
            return;
        }

        NotificationsEnabled = enabled;
        NotificationsEnabledChanged?.Invoke(this, EventArgs.Empty);
    }
}
