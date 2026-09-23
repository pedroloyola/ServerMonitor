using Microsoft.Windows.Globalization;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY. <c>--qa-ui-language en-US|pt-PT|pt-BR</c> forces the UI language for this launch, so each
/// culture can be verified on an unpackaged Debug build (where the in-app language choice does not
/// persist between launches — a known product limitation this does NOT fix). Applied in the App
/// constructor before the first ResourceLoader and before any window. Any other value is ignored.
/// Excluded from Release (see ServerMonitor.App.csproj); the flag is ignored there.
/// </summary>
internal static class QaUiLanguageComposition
{
    public static string? RequestedLanguage() =>
        QaUiLanguagePolicy.ResolveLanguage(Environment.GetCommandLineArgs(), isDebugBuild: true);

    public static void ApplyRequested()
    {
        if (RequestedLanguage() is { } language)
        {
            ApplicationLanguages.PrimaryLanguageOverride = language;
        }
    }
}
