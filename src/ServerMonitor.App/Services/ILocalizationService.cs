namespace ServerMonitor.App.Services;

public interface ILocalizationService
{
    string? CurrentLanguageOverride { get; }

    string GetString(string resourceKey);

    void InitializeFromSystem();

    void SetLanguage(string? languageTag);
}
