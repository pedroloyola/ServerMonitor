using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.Tests.TestSupport;

/// <summary>
/// F-1 scaffolding: what MRT Core loads the resw text in, by the documented rule (Microsoft Learn, "Understand user
/// profile languages and app manifest languages"): the app runtime language list is (1) the PrimaryLanguageOverride if
/// set, (2) otherwise the user profile language list filtered by the manifest languages, in profile order, (3) otherwise
/// the app's default language. A profile language that is a regional variant of a manifest language matches it (en-GB
/// → en-US resources, while the runtime language - and so the DOC formatting - is the user's en-GB).
/// <para>
/// The text in a test is otherwise invisible (a test host has no MRT context), so an end-to-end F-1 test asks this which
/// language the launch's override + the machine's profile would show. HYPOTHESIS, not verified on a VM: which Portuguese
/// resources MRT picks for a pt-* profile language that is neither pt-PT nor pt-BR (pt-AO, pt-MZ, ...); the simulator
/// uses the app's own mapping (<see cref="SupportedCultures.Resolve"/>: any non-Brazilian Portuguese → pt-PT).
/// </para>
/// </summary>
internal static class MrtRuntimeLanguages
{
    /// <summary>The manifest's resource languages (Package.appxmanifest; a test pins the two equal).</summary>
    public static IReadOnlyList<string> Manifest => SupportedCultures.All;

    /// <summary>The manifest language the resw text loads in.</summary>
    public static string ResourceLanguage(string? languageOverride, IReadOnlyList<string> profileLanguages)
    {
        ArgumentNullException.ThrowIfNull(profileLanguages);
        if (!string.IsNullOrWhiteSpace(languageOverride))
        {
            return ManifestMatch(languageOverride) ?? SupportedCultures.Default;
        }

        foreach (var language in profileLanguages)
        {
            if (ManifestMatch(language) is { } match)
            {
                return match;
            }
        }

        return SupportedCultures.Default;
    }

    /// <summary>
    /// The first app runtime language - per DOC, also the language dates, times and numbers format in: the override, or
    /// the first profile language with a manifest match (the user's own regional variant), or the default.
    /// </summary>
    public static string RuntimeLanguage(string? languageOverride, IReadOnlyList<string> profileLanguages)
    {
        ArgumentNullException.ThrowIfNull(profileLanguages);
        if (!string.IsNullOrWhiteSpace(languageOverride))
        {
            return languageOverride;
        }

        return profileLanguages.FirstOrDefault(language => ManifestMatch(language) is not null) ?? SupportedCultures.Default;
    }

    /// <summary>The manifest language a culture loads: exact, else same base language (en / pt), else none.</summary>
    public static string? ManifestMatch(string? culture)
    {
        var exact = Manifest.FirstOrDefault(language => string.Equals(language, culture, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var subtags = (culture ?? string.Empty).Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return subtags.Length > 0 && (subtags[0].Equals("en", StringComparison.OrdinalIgnoreCase)
                                      || subtags[0].Equals("pt", StringComparison.OrdinalIgnoreCase))
            ? SupportedCultures.Resolve(culture)
            : null;
    }
}
