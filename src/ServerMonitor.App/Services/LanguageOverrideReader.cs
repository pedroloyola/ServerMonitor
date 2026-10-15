namespace ServerMonitor.App.Services;

/// <summary>
/// REL.0D D-2 (VM QA, root cause; Cortex cortex-d2b.md): the language override in effect for this launch.
/// <para>
/// The Windows App SDK's <c>Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride</c> getter
/// returns a value held in the PROCESS only (WindowsAppSDK ApplicationLanguages.cpp: a static <c>m_language</c>); its
/// setter also forwards to <c>Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride</c> in a packaged
/// process, which is what Windows persists per package and MRT shows the text in. So in every new process of the
/// installed app the SDK getter read "" while the UI showed the saved choice: startup took "no override", recorded
/// System("") over an explicit choice, and on an unsupported Windows language even wrote the system override over it.
/// </para>
/// <list type="bullet">
/// <item>Packaged: the persisted per-package value (the SDK's process value if that read fails).</item>
/// <item>Unpackaged (Debug QA harness, tests): the SDK's process value, as before - nothing is persisted there, the
/// setter does not forward, and Windows has no per-package value to read.</item>
/// </list>
/// The setter stays on the SDK API, which sets both the process value (what MRT Core uses in-process) and, packaged,
/// the persisted one.
/// </summary>
internal static class LanguageOverrideReader
{
    private static readonly Lazy<bool> Packaged = new(DetectPackaged);

    public static string? Read(bool isPackaged, Func<string?> processOverride, Func<string?> persistedOverride)
    {
        ArgumentNullException.ThrowIfNull(processOverride);
        ArgumentNullException.ThrowIfNull(persistedOverride);
        if (!isPackaged)
        {
            return processOverride();
        }

        try
        {
            return persistedOverride();
        }
        catch (Exception)
        {
            return processOverride();
        }
    }

    /// <summary>The real sources: Windows' persisted package value when packaged, else the SDK's process value.</summary>
    public static string? ReadCurrent() => Read(
        Packaged.Value,
        static () => Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride,
        static () => global::Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride);

    /// <summary>Package identity, decided once (Package.Current throws without one, as AppVersionProvider relies on).</summary>
    private static bool DetectPackaged()
    {
        try
        {
            _ = global::Windows.ApplicationModel.Package.Current.Id;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
