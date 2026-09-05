namespace ServerMonitor.App.Services;

/// <summary>How this process was launched. Exactly two values, and no third is ever added (Vigil C4).</summary>
public enum LaunchMode
{
    /// <summary>The normal launch: the Dashboard is created and shown.</summary>
    Foreground,

    /// <summary>
    /// The headless launch: monitoring and the tray start, the Dashboard is NOT created, and a later
    /// legitimate activation materializes it (M13 S2 §B.2).
    /// </summary>
    Background
}

/// <summary>
/// Decides whether a launch is headless. Deliberately the smallest possible surface (Vigil C4):
/// <list type="bullet">
/// <item><b>Pure and stateless.</b> No fields, no clock, no environment, no I/O — the same arguments
/// always give the same answer, so the security review is a review of this one function.</item>
/// <item><b>A two-value codomain.</b> <see cref="LaunchMode"/> has exactly two members. Adding a third
/// mode, a parameter, a value or a second flag REOPENS the security opinion; the tests pin that.</item>
/// <item><b>Exact token match.</b> Only the literal <c>--background</c> (case-insensitive) selects the
/// headless mode. Not a prefix, not <c>--background=1</c>, not <c>--background:x</c>, not
/// <c>-background</c>, not <c>--backgroundx</c>. There is no value grammar to smuggle anything through,
/// so an attacker-controlled command line has no reachable surface here beyond flipping a boolean that
/// only decides whether a window is created.</item>
/// </list>
/// This is the S2 half of the headless work: the safe TARGET. Deciding which SOURCES may start such a
/// process (startup task, logon, provider-initiated launch) is S4 and is deliberately absent.
/// </summary>
public static class LaunchModePolicy
{
    /// <summary>The one recognized token. Nothing else selects <see cref="LaunchMode.Background"/>.</summary>
    public const string BackgroundSwitch = "--background";

    /// <summary>
    /// Classifies a launch from its command-line arguments (including or excluding argv[0] — the switch
    /// is matched by value, never by position).
    /// </summary>
    public static LaunchMode Resolve(IReadOnlyList<string>? commandLineArgs)
    {
        if (commandLineArgs is null)
        {
            return LaunchMode.Foreground;
        }

        for (var index = 0; index < commandLineArgs.Count; index++)
        {
            if (IsBackgroundSwitch(commandLineArgs[index]))
            {
                return LaunchMode.Background;
            }
        }

        return LaunchMode.Foreground;
    }

    /// <summary>
    /// Classifies a launch from a raw command-line string, which is the shape a REDIRECTED activation
    /// carries (<c>ILaunchActivatedEventArgs.Arguments</c>). Splitting on whitespace is enough because
    /// the only thing being looked for is a standalone token: a quoted path containing the token as a
    /// substring cannot match, since the comparison is on the whole token.
    /// </summary>
    public static LaunchMode ResolveFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return LaunchMode.Foreground;
        }

        var tokens = commandLine.Split(
            [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return Resolve(tokens);
    }

    private static bool IsBackgroundSwitch(string? argument) =>
        string.Equals(argument, BackgroundSwitch, StringComparison.OrdinalIgnoreCase);
}
