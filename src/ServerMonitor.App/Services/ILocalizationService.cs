namespace ServerMonitor.App.Services;

public interface ILocalizationService
{
    string? CurrentLanguageOverride { get; }

    /// <summary>
    /// REL.0D: true when the UI follows the Windows language (the picker's "Use system language"). The real service
    /// also counts an override the app itself wrote for the system language; the default is "no override".
    /// </summary>
    bool FollowsSystemLanguage => CurrentLanguageOverride is null;

    /// <summary>
    /// REL.0D: the language the one-time legacy hint offers (the effective system language, e.g. en-US on fr-FR), or
    /// null when no hint is due. Only the real service can tell; the default is never.
    /// </summary>
    string? PendingLegacyLanguageHint => null;

    string GetString(string resourceKey);

    void InitializeFromSystem();

    void SetLanguage(string? languageTag);

    /// <summary>REL.0D: <see cref="SetLanguage"/> that reports whether the choice was saved (false = nothing changed).</summary>
    bool TrySetLanguage(string? languageTag)
    {
        SetLanguage(languageTag);
        return true;
    }

    /// <summary>
    /// REL.0D: records the current language as the user's own choice without changing it (the legacy hint's "Keep").
    /// False when it could not be saved.
    /// </summary>
    bool KeepCurrentLanguage() => true;
}
