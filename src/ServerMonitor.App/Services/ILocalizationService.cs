namespace ServerMonitor.App.Services;

public interface ILocalizationService
{
    string? CurrentLanguageOverride { get; }

    void InitializeFromSystem();

    void SetLanguage(string? languageTag);
}
