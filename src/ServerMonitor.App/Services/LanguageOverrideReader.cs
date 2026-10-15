using System.Runtime.InteropServices;

namespace ServerMonitor.App.Services;

/// <summary>Whether this process has a package identity - or that could not be determined.</summary>
internal enum PackageIdentity
{
    Packaged,
    Unpackaged,
    Unknown
}

/// <summary>The language override as read: its value, and whether it could be read at all.</summary>
internal readonly record struct LanguageOverrideRead(string? Value, bool IsKnown);

/// <summary>
/// REL.0D D-2 (VM QA, root cause; Cortex cortex-d2b.md): the language override in effect for this launch, and how to
/// change it.
/// <para>
/// The Windows App SDK's <c>Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride</c> getter
/// returns a value held in the PROCESS only (WindowsAppSDK ApplicationLanguages.cpp: a static <c>m_language</c>); its
/// setter also forwards to <c>Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride</c> in a packaged
/// process, which is what Windows persists per package and MRT shows the text in. So in every new process of the
/// installed app the SDK getter read "" while the UI showed the saved choice: startup took "no override", recorded
/// System("") over an explicit choice, and on an unsupported Windows language even wrote the system override over it.
/// </para>
/// <list type="bullet">
/// <item>Read, packaged: the persisted per-package value. Unpackaged (Debug QA harness, tests): the SDK's process value,
/// as before - nothing is persisted there. PR #59 round 2 (Vigil L-1): when the persisted read or the identity check
/// fails the result is NOT KNOWN - the process value is only a display fallback, and startup must not write on it.</item>
/// <item>Write: the SDK setter for a language (packaged it persists it too). PR #59 round 2 (Cortex M-1): the SDK setter
/// REJECTS "" (IsWellFormedTag → E_INVALIDARG), so clearing - "Use system language" on a supported Windows language -
/// resets the persisted value in Windows directly when packaged, and is a no-op unpackaged (nothing persisted to clear;
/// the next process starts without one anyway).</item>
/// </list>
/// </summary>
internal static class LanguageOverrideReader
{
    private const int AppModelErrorNoPackage = 15700;     // APPMODEL_ERROR_NO_PACKAGE
    private const int ErrorInsufficientBuffer = 122;

    private static readonly Lazy<PackageIdentity> Identity = new(DetectIdentity);

    public static LanguageOverrideRead Read(PackageIdentity identity, Func<string?> processOverride, Func<string?> persistedOverride)
    {
        ArgumentNullException.ThrowIfNull(processOverride);
        ArgumentNullException.ThrowIfNull(persistedOverride);
        switch (identity)
        {
            case PackageIdentity.Unpackaged:
                return new(processOverride(), IsKnown: true);
            case PackageIdentity.Packaged:
                try
                {
                    return new(persistedOverride(), IsKnown: true);
                }
                catch (Exception)
                {
                    return new(processOverride(), IsKnown: false);
                }

            default:
                return new(processOverride(), IsKnown: false);
        }
    }

    public static void Write(PackageIdentity identity, string? language, Action<string> sdkSetter, Action<string> persistedSetter)
    {
        ArgumentNullException.ThrowIfNull(sdkSetter);
        ArgumentNullException.ThrowIfNull(persistedSetter);
        if (string.IsNullOrEmpty(language))
        {
            if (identity == PackageIdentity.Packaged)
            {
                persistedSetter(string.Empty);
            }

            return;
        }

        sdkSetter(language);
    }

    /// <summary>The real sources: Windows' persisted package value when packaged, else the SDK's process value.</summary>
    public static LanguageOverrideRead ReadCurrent() => Read(
        Identity.Value,
        static () => Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride,
        static () => global::Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride);

    /// <summary>The real targets: the SDK setter for a language, Windows' persisted value to clear it.</summary>
    public static void WriteCurrent(string? language) => Write(
        Identity.Value,
        language,
        static value => Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = value,
        static value => global::Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = value);

    /// <summary>
    /// Package identity, decided once. <c>GetCurrentPackageFullName</c> answers "no package" with its own error code,
    /// so "unpackaged" is never confused with a failed check (Vigil L-1): anything else unexpected is Unknown.
    /// </summary>
    private static PackageIdentity DetectIdentity()
    {
        try
        {
            uint length = 0;
            return GetCurrentPackageFullName(ref length, null) switch
            {
                AppModelErrorNoPackage => PackageIdentity.Unpackaged,
                ErrorInsufficientBuffer or 0 => PackageIdentity.Packaged,
                _ => PackageIdentity.Unknown
            };
        }
        catch (Exception)
        {
            return PackageIdentity.Unknown;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, [Out] char[]? packageFullName);
}
