using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>
/// Startup cleanup of the ONE temporary file that a watchdog termination can orphan (Vigil C10).
/// <para>
/// The host-key trust store writes <c>known-hosts.json</c> by creating <c>known-hosts.json.tmp</c> and
/// moving it over the destination, deleting the temp in a <c>finally</c>. That <c>finally</c> is the only
/// thing that removes it, and <c>TerminateProcess</c> does not run <c>finally</c> blocks — so a watchdog
/// kill during a trust write is exactly the case that can leave the temp behind. Nothing else can.
/// </para>
/// <para>
/// The cleanup is therefore deliberately the narrowest possible: <b>one exact absolute path</b>, derived
/// from the store's own options, deleted only if it exists. No directory sweep, no wildcard, no pattern
/// beyond the writer's proven contract, and no file this app did not itself create is ever touched. A
/// failure is logged and ignored — a leftover temp is harmless, and deleting more than we can prove we
/// own would not be.
/// </para>
/// </summary>
public sealed class OrphanTemporaryCleaner(ILogger<OrphanTemporaryCleaner> logger)
{
    /// <summary>The writer's suffix. Kept next to the cleanup so the two cannot drift apart silently.</summary>
    public const string TemporarySuffix = ".tmp";

    /// <summary>
    /// Removes the known-host temporary if it is present. <paramref name="knownHostsPath"/> is the exact
    /// destination path the trust store uses; the temporary is that path plus the writer's suffix.
    /// </summary>
    public void CleanKnownHostTemporary(string knownHostsPath)
    {
        if (string.IsNullOrWhiteSpace(knownHostsPath))
        {
            return;
        }

        var temporaryPath = knownHostsPath + TemporarySuffix;
        try
        {
            if (!File.Exists(temporaryPath))
            {
                return;
            }

            File.Delete(temporaryPath);
            logger.LogInformation("Removed a host-key temporary left by a previous forced termination.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Harmless: the next successful write replaces it anyway.
            logger.LogWarning(
                "A host-key temporary could not be removed ({Type}); leaving it in place.",
                exception.GetType().Name);
        }
    }
}
