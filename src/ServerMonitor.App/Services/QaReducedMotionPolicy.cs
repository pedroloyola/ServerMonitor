namespace ServerMonitor.App.Services;

/// <summary>
/// UI.11 (Cortex §4, PH-1). The Debug-only <c>--qa-reduced-motion</c> modifier pins Reduced Motion ON for one QA launch,
/// so every motion pattern can be verified in its instant form WITHOUT changing the user's system "Animation effects"
/// setting (which is global to the session). It is a modifier, never a harness: on its own it is refused like every
/// other modifier (QaStartupIsolation), and next to a gallery flag it is one of the gallery's own options.
/// Strict: exactly the lower-case flag, no value, at most once; anything else is refused, never silently ignored.
/// Compiled in every configuration so a test can prove the Release guard: with <c>isDebugBuild: false</c> it is never on.
/// </summary>
public static class QaReducedMotionPolicy
{
    public const string LaunchFlag = "--qa-reduced-motion";

    /// <summary>True only in Debug, for a launch that carries the exact flag exactly once and no malformed form of it.</summary>
    public static bool IsRequested(IReadOnlyList<string> commandLineArgs, bool isDebugBuild)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);
        return isDebugBuild
            && Refusal(commandLineArgs) is null
            && commandLineArgs.Contains(LaunchFlag, StringComparer.Ordinal);
    }

    /// <summary>Null when the flag is absent or well-formed; otherwise why the launch must be refused.</summary>
    public static string? Refusal(IReadOnlyList<string> commandLineArgs)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);
        var mentions = commandLineArgs
            .Where(argument => string.Equals(argument.Split('=', 2)[0], LaunchFlag, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (mentions.Any(argument => !string.Equals(argument, LaunchFlag, StringComparison.Ordinal)))
        {
            return $"{LaunchFlag} takes no value and must be written exactly '{LaunchFlag}'.";
        }

        return mentions.Count > 1 ? $"{LaunchFlag} may be given at most once." : null;
    }
}
