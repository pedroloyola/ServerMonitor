namespace ServerMonitor.App.Services;

public sealed record NotificationSettingsStorageOptions
{
    public required string FilePath { get; init; }

    public static NotificationSettingsStorageOptions ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return new NotificationSettingsStorageOptions
        {
            FilePath = Path.Combine(localApplicationData, "ServerMonitor", "notification-settings.json")
        };
    }
}
