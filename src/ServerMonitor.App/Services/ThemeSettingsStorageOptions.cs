namespace ServerMonitor.App.Services;

/// <summary>THEME-1: where the saved theme choice lives (next to <c>language-settings.json</c>).</summary>
public sealed record ThemeSettingsStorageOptions
{
    public required string FilePath { get; init; }

    public static ThemeSettingsStorageOptions ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return new ThemeSettingsStorageOptions
        {
            FilePath = Path.Combine(localApplicationData, "ServerMonitor", "theme-settings.json")
        };
    }
}
