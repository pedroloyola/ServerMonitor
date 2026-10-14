using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Services;

/// <summary>
/// REL.0B (UI9-FW-PROMPT): the one place that ends a user-started local-network search when the window leaves
/// the foreground (close to tray, minimize to tray). Nothing here ever STARTS discovery - that is only
/// <see cref="IServerDiscoveryActivity.StartSearchAsync"/>, from an explicit user action.
/// </summary>
internal static class LocalNetworkSearchLifetime
{
    /// <summary>
    /// Fire-and-forget stop on the hide path: never throws and never blocks the UI thread. A composition without
    /// live discovery (the inert default, the QA stand-ins) has nothing to stop.
    /// </summary>
    public static Task StopForBackground(IServerDiscoveryService discoveryService, ILogger logger)
    {
        // Not gated on IsSearching: a start still in flight when the window hides would otherwise finish AFTER the
        // hide and leave the listener running in the background. Stop is idempotent and queues behind that start.
        if (discoveryService is not IServerDiscoveryActivity activity)
        {
            return Task.CompletedTask;
        }

        return StopAsync(activity, logger);
    }

    private static async Task StopAsync(IServerDiscoveryActivity activity, ILogger logger)
    {
        try
        {
            await activity.StopSearchAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Local network search did not stop cleanly on hide. Exception type: {Type}.",
                exception.GetType().Name);
        }
    }
}
