namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// F-1 (REL.0D, VM QA): the two Windows language sources, kept SEPARATE - the Windows display language (what .NET's
/// <c>CultureInfo.CurrentUICulture</c> and the widget provider read) and the ordered "Preferred languages" profile list
/// (what MRT matches against the manifest when there is no override). They only agree when the display language is the
/// first preferred one; F-1 is the case where they do not. Since the fix (Cortex A-lite) the app's decisions, number
/// format and widget read the profile language, the display only the legacy-hint condition. Never the user's real settings.
/// </summary>
internal sealed record FakeWindowsLanguages(string DisplayCulture, IReadOnlyList<string> ProfileLanguages)
{
    public static FakeWindowsLanguages Display(string displayCulture, params string[] profileLanguages) =>
        new(displayCulture, profileLanguages.Length == 0 ? [displayCulture] : profileLanguages);

    /// <summary>True for the common machine: the display language is also the first preferred language.</summary>
    public bool SourcesAgree =>
        ProfileLanguages.Count > 0 && string.Equals(ProfileLanguages[0], DisplayCulture, StringComparison.OrdinalIgnoreCase);

    /// <summary>The profile language the real platform derives from this machine (Cortex A-lite).</summary>
    public string ProfileLanguage => Core.Domain.SupportedCultures.ProfileLanguage(ProfileLanguages, DisplayCulture);

    /// <summary>
    /// The app's platform on this machine, as the real one builds it: the profile language as the "system language",
    /// the display language kept separately (the legacy-hint condition and the number-format comparison).
    /// </summary>
    public FakeUiLanguagePlatform Platform(string? persistedOverride = null, List<string>? journal = null) =>
        new(ProfileLanguage, persistedOverride, journal) { DisplayCultureName = DisplayCulture };

    public override string ToString() => $"display {DisplayCulture} / profile [{string.Join(", ", ProfileLanguages)}]";
}
