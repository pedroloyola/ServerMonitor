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

    /// <summary>The Windows UI culture name (empty for the invariant culture).</summary>
    string SystemUiCultureName { get; }

    /// <summary>
    /// REL.0D D-2, PR #59 round 2 (Vigil L-1): false when the persisted override could not be read (or the package
    /// identity could not be determined). <see cref="PrimaryLanguageOverride"/> is then only a display fallback, and the
    /// startup resolution writes nothing - like an unreadable provenance record. True for any platform that always knows.
    /// </summary>
    bool IsOverrideKnown => true;
}
