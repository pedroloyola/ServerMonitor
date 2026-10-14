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
/// No override means the Windows display language is already the UI language: nothing is changed. The Windows culture
/// itself stays readable for the language decisions through the snapshot the real <see cref="IUiLanguagePlatform"/>
/// takes when it is built, before this runs.
/// </para>
/// </summary>
internal static class AppFormattingCulture
{
    /// <summary>The culture to format in for <paramref name="languageOverride"/>; null when there is none to apply.</summary>
    public static CultureInfo? For(string? languageOverride)
    {
        if (string.IsNullOrWhiteSpace(languageOverride))
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(languageOverride);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Hands the culture for <paramref name="languageOverride"/>, if any, to <paramref name="applyUiCulture"/>.</summary>
    public static void Apply(string? languageOverride, Action<CultureInfo> applyUiCulture)
    {
        ArgumentNullException.ThrowIfNull(applyUiCulture);
        if (For(languageOverride) is { } culture)
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
