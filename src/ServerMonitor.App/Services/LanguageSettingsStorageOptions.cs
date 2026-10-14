namespace ServerMonitor.App.Services;

/// <summary>REL.0D: where the language provenance record lives (next to the other small settings files).</summary>
public sealed record LanguageSettingsStorageOptions
{
    public required string FilePath { get; init; }

    public static LanguageSettingsStorageOptions ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return new LanguageSettingsStorageOptions
        {
            FilePath = Path.Combine(localApplicationData, "ServerMonitor", "language-settings.json")
        };
    }
}
