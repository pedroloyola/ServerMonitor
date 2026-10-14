namespace ServerMonitor.App.Services;

/// <summary>
/// REL.0C: the platform boundary of <see cref="LocalizationService"/> - the persisted app language override and the
/// Windows UI culture. Production uses the Windows App SDK (<c>ApplicationLanguages.PrimaryLanguageOverride</c>, which
/// persists in a packaged app) and <c>CultureInfo.CurrentUICulture</c>; tests use a fake, so the real startup
/// resolution runs without touching the user's Windows language preferences.
/// </summary>
internal interface IUiLanguagePlatform
{
    /// <summary>The app's language override; empty or null when none is set.</summary>
    string? PrimaryLanguageOverride { get; set; }

    /// <summary>The Windows UI culture name (empty for the invariant culture).</summary>
    string SystemUiCultureName { get; }
}
