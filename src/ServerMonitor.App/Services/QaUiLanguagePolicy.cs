using ServerMonitor.Core.Domain;

namespace ServerMonitor.App.Services;

/// <summary>
/// Decides whether a launch asked the Debug-only QA harness to force the UI language
/// (<c>--qa-ui-language &lt;tag&gt;</c> or <c>--qa-ui-language=&lt;tag&gt;</c>), so en-US / pt-PT / pt-BR can be
/// checked on an unpackaged build, where the language override does not persist between launches.
/// Compiled in every configuration so a test can prove the Release guard: with
/// <c>isDebugBuild: false</c> the flag is always ignored.
/// </summary>
public static class QaUiLanguagePolicy
{
    public const string LaunchFlag = "--qa-ui-language";

    /// <summary>
    /// The canonical supported tag (en-US, pt-PT or pt-BR), or <see langword="null"/> in Release, without
    /// the flag, or for any other value (never falls back to a default language).
    /// </summary>
    public static string? ResolveLanguage(IReadOnlyList<string> commandLineArgs, bool isDebugBuild)
    {
        if (!isDebugBuild)
        {
            return null;
        }

        for (var index = 0; index < commandLineArgs.Count; index++)
        {
            var argument = commandLineArgs[index];
            string? value;
            if (string.Equals(argument, LaunchFlag, StringComparison.OrdinalIgnoreCase))
            {
                value = index + 1 < commandLineArgs.Count ? commandLineArgs[index + 1] : null;
            }
            else if (argument.StartsWith(LaunchFlag + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument[(LaunchFlag.Length + 1)..];
            }
            else
            {
                continue;
            }

            return SupportedCultures.All.FirstOrDefault(
                culture => string.Equals(culture, value, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }
}
