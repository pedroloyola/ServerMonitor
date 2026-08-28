namespace ServerMonitor.App.Services;

/// <summary>
/// Decides the single-instance key for a launch. Pure and deterministic so it can be unit-tested
/// without the WinRT <c>AppInstance</c> singleton (which only a real smoke launch can exercise —
/// see L-016). Production is always single-instanced (M12/ADR-017 §6); Debug QA harnesses bypass it
/// so <c>--qa-*</c> runs never contend with a normal instance (§20).
/// </summary>
public static class SingleInstancePolicy
{
    /// <summary>Stable, product-neutral instance key for the production app.</summary>
    public const string ProductionInstanceKey = "ServerMonitor";

    /// <summary>
    /// Returns the key to register with <c>AppInstance.FindOrRegisterForKey</c>, or <c>null</c> to
    /// bypass single-instancing entirely (allow multiple instances).
    /// </summary>
    public static string? ResolveInstanceKey(IReadOnlyList<string> commandLineArgs, bool isDebugBuild)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);

        if (isDebugBuild && HasQaArgument(commandLineArgs))
        {
            // Debug-only bypass: QA harnesses may run alongside a normal instance.
            return null;
        }

        return ProductionInstanceKey;
    }

    /// <summary>True when any argument selects a QA harness (<c>--qa-*</c>).</summary>
    public static bool HasQaArgument(IReadOnlyList<string> commandLineArgs)
    {
        ArgumentNullException.ThrowIfNull(commandLineArgs);

        for (var index = 0; index < commandLineArgs.Count; index++)
        {
            if (commandLineArgs[index].StartsWith("--qa-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
