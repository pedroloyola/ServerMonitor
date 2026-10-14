namespace ServerMonitor.Core.Domain;

/// <summary>
/// The three UI languages and the single table that maps any Windows culture onto one of them (REL.0C):
/// <c>pt-BR</c> → pt-BR; any other Portuguese (<c>pt</c>, <c>pt-PT</c>, <c>pt-AO</c>, …) → pt-PT;
/// everything else, English variants and unsupported languages alike → en-US, the package default.
/// The widget card (<c>WidgetStrings.ForCulture</c>) follows the same table.
/// </summary>
public static class SupportedCultures
{
    public const string Default = "en-US";

    public const string EuropeanPortuguese = "pt-PT";

    public const string BrazilianPortuguese = "pt-BR";

    public static IReadOnlyList<string> All { get; } = [Default, EuropeanPortuguese, BrazilianPortuguese];

    public static bool IsSupported(string? cultureName) =>
        All.Contains(cultureName ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps a culture name to a supported language by base language; never returns null.</summary>
    public static string Resolve(string? cultureName)
    {
        var subtags = (cultureName ?? string.Empty).Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (subtags.Length == 0 || !string.Equals(subtags[0], "pt", StringComparison.OrdinalIgnoreCase))
        {
            return Default;
        }

        // The region may follow a script subtag (pt-Latn-BR); only Brazil gets Brazilian Portuguese.
        return subtags.Skip(1).Any(subtag => string.Equals(subtag, "BR", StringComparison.OrdinalIgnoreCase))
            ? BrazilianPortuguese
            : EuropeanPortuguese;
    }

    /// <summary>
    /// The language override to write at startup, or <see langword="null"/> to write nothing. A persisted
    /// override is the user's choice and is never touched (also a pt-BR written by the old pt-BR default:
    /// existing users are not migrated). Without one, an exactly supported system culture needs no override;
    /// any other culture gets the <see cref="Resolve"/> mapping so the UI language is deterministic.
    /// </summary>
    public static string? StartupOverride(string? persistedOverride, string? systemCulture)
    {
        if (!string.IsNullOrWhiteSpace(persistedOverride) || IsSupported(systemCulture))
        {
            return null;
        }

        return Resolve(systemCulture);
    }
}
