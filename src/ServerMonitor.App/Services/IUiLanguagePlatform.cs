namespace ServerMonitor.App.Services;

/// <summary>
/// REL.0C: the platform boundary of <see cref="LocalizationService"/> - the persisted app language override and the
/// Windows UI culture. Production uses the Windows App SDK (<c>ApplicationLanguages.PrimaryLanguageOverride</c>, which
/// persists in a packaged app) and <c>CultureInfo.CurrentUICulture</c> as it was at startup (REL.0E: before the app
/// language becomes the process UI culture); tests use a fake, so the real startup
/// resolution runs without touching the user's Windows language preferences.
/// </summary>
internal interface IUiLanguagePlatform
{
    /// <summary>The app's language override; empty or null when none is set.</summary>
    string? PrimaryLanguageOverride { get; set; }

    /// <summary>
    /// The user's "system language" for this app (F-1, Cortex A-lite): the profile language
    /// (<see cref="Core.Domain.SupportedCultures.ProfileLanguage"/> of the preferred-language list) - the source MRT loads
    /// the text from without an override. Every language decision, the number format and "Use system language" read it.
    /// Empty for the invariant culture.
    /// </summary>
    string SystemUiCultureName { get; }

    /// <summary>
    /// F-1: the Windows DISPLAY language (<c>CultureInfo.CurrentUICulture</c> at startup). Only the legacy-hint CONDITION
    /// reads it (v1.x decided by it), and the number format compares against it (.NET formats in it unless told
    /// otherwise). By default the same as <see cref="SystemUiCultureName"/>: the common machine, display = first preferred.
    /// </summary>
    string DisplayCultureName => SystemUiCultureName;

    /// <summary>
    /// REL.0D D-2, PR #59 round 2 (Vigil L-1): false when the persisted override could not be read (or the package
    /// identity could not be determined). <see cref="PrimaryLanguageOverride"/> is then only a display fallback, and the
    /// startup resolution writes nothing - like an unreadable provenance record. True for any platform that always knows.
    /// </summary>
    bool IsOverrideKnown => true;
}
