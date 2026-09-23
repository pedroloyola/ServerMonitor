namespace ServerMonitor.App.Services;

/// <summary>
/// Decides whether a launch asked for the Debug-only SSH config QA harness
/// (<c>--qa-ssh-config &lt;dir&gt;</c> or <c>--qa-ssh-config=&lt;dir&gt;</c>) and which fixture profile
/// directory it names. Compiled in every configuration so a test can prove the Release guard: with
/// <c>isDebugBuild: false</c> the flag is always ignored, whatever the arguments.
/// </summary>
public static class QaSshConfigProfilePolicy
{
    public const string LaunchFlag = "--qa-ssh-config";

    /// <summary>The fully-qualified fixture profile, or <see langword="null"/> (Release, no flag, or no usable directory).</summary>
    public static string? ResolveProfile(IReadOnlyList<string> commandLineArgs, bool isDebugBuild)
    {
        if (!isDebugBuild)
        {
            return null;
        }

        for (var index = 0; index < commandLineArgs.Count; index++)
        {
            var argument = commandLineArgs[index];
            string? value = null;
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

            // A fixture directory only: a relative or empty value is refused rather than guessed.
            return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
                ? Path.GetFullPath(value)
                : null;
        }

        return null;
    }
}
