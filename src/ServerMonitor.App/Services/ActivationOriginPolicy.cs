namespace ServerMonitor.App.Services;

/// <summary>
/// Classifies WHO asked for an activation, from the raw launch arguments (M13 S2 §H.2, corrected).
/// <para>
/// It exists so the grammar can be tested end to end. The previous round fed already-classified
/// <see cref="ActivationOrigin"/> values into the dispatch matrix, which proved the dispatch but not the
/// classification — the actual question ("is this command line a background launch?") went untested.
/// This is the one place that answers it, it is pure, and it delegates the token grammar to
/// <see cref="LaunchModePolicy"/> so there is exactly one definition of the switch in the product.
/// </para>
/// </summary>
public static class ActivationOriginPolicy
{
    /// <summary>
    /// A redirected plain launch carries its command line. Only an exact <c>--background</c> token makes
    /// it a background launch; everything else — no arguments, unknown flags, a value form, a protocol
    /// activation with no launch arguments at all — is a person doing something, which is the
    /// conservative answer: at worst the app shows itself when asked to, never the reverse.
    /// </summary>
    public static ActivationOrigin FromLaunchCommandLine(string? commandLine) =>
        LaunchModePolicy.ResolveFromCommandLine(commandLine) == LaunchMode.Background
            ? ActivationOrigin.BackgroundLaunch
            : ActivationOrigin.UserActivation;
}
