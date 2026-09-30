namespace ServerMonitor.App.Services;

/// <summary>
/// Decides whether a launch asked for the Debug-only ProxyJump editor QA harness
/// (<c>--qa-proxyjump</c>) and which isolated data directory it names
/// (<c>--qa-proxyjump-dir &lt;dir&gt;</c> or <c>--qa-proxyjump-dir=&lt;dir&gt;</c>). Compiled in every
/// configuration so a test can prove the Release guard: with <c>isDebugBuild: false</c> the flag is always
/// ignored, whatever the arguments.
/// </summary>
public static class QaProxyJumpPolicy
{
    public const string LaunchFlag = "--qa-proxyjump";

    public const string DirectoryFlag = "--qa-proxyjump-dir";

    /// <summary>True when a Debug launch carries <see cref="LaunchFlag"/>.</summary>
    public static bool IsRequested(IReadOnlyList<string> commandLineArgs, bool isDebugBuild)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);

        return isDebugBuild && commandLineArgs.Any(
            argument => string.Equals(argument, LaunchFlag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The fully-qualified isolated data directory, or <see langword="null"/> (Release, no flag, or no
    /// usable directory). A relative or empty value is refused rather than guessed: the harness exists to
    /// keep the real profile untouched, so it must never resolve to a path the caller did not name.
    /// </summary>
    public static string? ResolveDirectory(IReadOnlyList<string> commandLineArgs, bool isDebugBuild)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);

        if (!isDebugBuild)
        {
            return null;
        }

        for (var index = 0; index < commandLineArgs.Count; index++)
        {
            var argument = commandLineArgs[index];
            string? value = null;
            if (string.Equals(argument, DirectoryFlag, StringComparison.OrdinalIgnoreCase))
            {
                value = index + 1 < commandLineArgs.Count ? commandLineArgs[index + 1] : null;
            }
            else if (argument.StartsWith(DirectoryFlag + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument[(DirectoryFlag.Length + 1)..];
            }
            else
            {
                continue;
            }

            return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
                ? Path.GetFullPath(value)
                : null;
        }

        return null;
    }
}
