using System.Globalization;

namespace ServerMonitor.App.Services;

/// <summary>
/// REL.0E (Sentry, number format): .NET formatting follows the app's UI language.
/// <para>
/// The resw copy follows the Windows App SDK override (<c>ApplicationLanguages.PrimaryLanguageOverride</c>), but every
/// <c>string.Format(CultureInfo.CurrentUICulture, ...)</c> followed the Windows display language, which the override
/// does not change: English chosen in Settings on a Portuguese Windows read "6,6 GB of 16 GB". Microsoft's model is that
/// the app's runtime language formats numbers and dates too, so once the startup language is final (it changes only on
/// a restart) the process UI culture is set to it. <see cref="CultureInfo.CurrentCulture"/> (the regional format) is
/// left alone.
/// </para>
/// <para>
/// WHICH culture is <see cref="ILocalizationService.FormattingCultureName"/> (fix round 1, Cortex M-1): null - no
/// override, or one the app wrote for a regional variant of the Windows language - changes nothing. The Windows culture
/// itself stays readable for the language decisions through the snapshot the real <see cref="IUiLanguagePlatform"/>
/// takes when it is built, before this runs.
/// </para>
/// </summary>
internal static class AppFormattingCulture
{
    /// <summary>
    /// The culture to format in for <paramref name="cultureName"/>; null when there is none to apply. Built with the
    /// user's Region customisations (Sentry nit): .NET applies them only when the name is the user's own regional format,
    /// exactly as it did for the Windows culture this replaces; read-only, as a shared process culture should be.
    /// </summary>
    public static CultureInfo? For(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return null;
        }

        try
        {
            return CultureInfo.ReadOnly(new CultureInfo(cultureName, useUserOverride: true));
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Hands the culture for <paramref name="cultureName"/>, if any, to <paramref name="applyUiCulture"/>.</summary>
    public static void Apply(string? cultureName, Action<CultureInfo> applyUiCulture)
    {
        ArgumentNullException.ThrowIfNull(applyUiCulture);
        if (For(cultureName) is { } culture)
        {
            applyUiCulture(culture);
        }
    }

    /// <summary>The production setter: the startup thread and every thread created or pooled afterwards.</summary>
    public static void SetProcessUiCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
