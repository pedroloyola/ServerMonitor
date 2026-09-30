namespace ServerMonitor.App.Services;

/// <summary>
/// Decides whether a launch asked the Debug-only backup/restore QA harness
/// (<c>--qa-backup &lt;scenario&gt;</c> or <c>--qa-backup=&lt;scenario&gt;</c>), which replaces the backup
/// engine and its file pickers with in-memory doubles so every M14.6 UI state can be reached without
/// touching real configuration, credentials or files. Compiled in every configuration so a test can
/// prove the Release guard: with <c>isDebugBuild: false</c> the flag is always ignored.
/// </summary>
public static class QaBackupPolicy
{
    public const string LaunchFlag = "--qa-backup";

    public static readonly IReadOnlyList<string> Scenarios =
        ["ok", "rollback", "partial", "invalid", "stuck", "recovered"];

    /// <summary>The canonical scenario, or <see langword="null"/> in Release, without the flag, or for
    /// any unknown value (never falls back to a default scenario).</summary>
    public static string? ResolveScenario(IReadOnlyList<string> commandLineArgs, bool isDebugBuild)
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

            return Scenarios.FirstOrDefault(
                scenario => string.Equals(scenario, value, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }
}
