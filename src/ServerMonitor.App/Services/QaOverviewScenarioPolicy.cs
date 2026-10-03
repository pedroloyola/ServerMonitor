namespace ServerMonitor.App.Services;

/// <summary>
/// UI.4: which deterministic scenario the Debug-only overview harness (<c>--qa-overview</c>) seeds, selected by the
/// modifier <c>--qa-overview-scenario &lt;name&gt;</c> or <c>--qa-overview-scenario=&lt;name&gt;</c>. Compiled in every
/// configuration so a test can prove the Release guard: with <c>isDebugBuild: false</c> the modifier is always ignored.
/// The Servidores states (search / no results) are interactions over <c>mixed</c> and <c>many-100</c>; empty is
/// <c>empty</c>; 100 / 500 rows are <c>many-100</c> / <c>many-500</c>. <c>vanishing</c> is <c>mixed</c> whose servers
/// all disappear 30 s after the first load, so the Servidores empty state (§13, reachable only with the page open) can
/// be seen without a dialog.
/// <para>
/// UI.5: <c>detail</c> / <c>detail-failing</c> hold one server per Server Detail state (incl. the connection-state store's
/// auth / host-key results and a ProxyJump server) over MUTATING in-memory doubles — hide / restore / remove / refresh
/// succeed in <c>detail</c> and fail in <c>detail-failing</c>. <c>data</c> / <c>data-failing</c> are the Settings
/// "Dados e servidores" states (hidden servers, restore succeeding / failing). Every <c>--qa-overview</c> launch, whatever
/// its scenario, REQUIRES <c>--qa-backup</c> (UI.5 Boss B2 answer 5): Settings is one click away from any scenario, so the
/// real backup engine and its native file picker can never be reached from this harness.
/// </para>
/// </summary>
public static class QaOverviewScenarioPolicy
{
    public const string LaunchFlag = "--qa-overview-scenario";

    /// <summary>The scenario used when the harness is launched without the modifier (the Figma frame's six servers).</summary>
    public const string DefaultScenario = "mixed";

    public static readonly IReadOnlyList<string> Scenarios =
    [
        "healthy", "mixed", "attention", "critical", "offline", "empty", "loading", "unavailable", "discovery",
        "many-100", "many-500", "vanishing", "detail", "detail-failing", "data", "data-failing"
    ];


    /// <summary>True when the modifier is present at all (whatever its value).</summary>
    public static bool IsPresent(IReadOnlyList<string> commandLineArgs) =>
        commandLineArgs.Any(argument => string.Equals(argument, LaunchFlag, StringComparison.Ordinal)
            || argument.StartsWith(LaunchFlag + "=", StringComparison.Ordinal));

    /// <summary>
    /// The canonical scenario, or <see langword="null"/> in Release, without the modifier, or for any unknown value
    /// (never falls back to a default — the harness refuses an unknown scenario instead).
    /// </summary>
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
            if (string.Equals(argument, LaunchFlag, StringComparison.Ordinal))
            {
                value = index + 1 < commandLineArgs.Count ? commandLineArgs[index + 1] : null;
            }
            else if (argument.StartsWith(LaunchFlag + "=", StringComparison.Ordinal))
            {
                value = argument[(LaunchFlag.Length + 1)..];
            }
            else
            {
                continue;
            }

            return Scenarios.FirstOrDefault(
                scenario => string.Equals(scenario, value, StringComparison.Ordinal));
        }

        return null;
    }
}
