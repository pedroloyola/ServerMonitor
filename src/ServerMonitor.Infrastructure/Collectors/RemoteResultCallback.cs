namespace ServerMonitor.Infrastructure.Collectors;

/// <summary>Calls a phase-1 callback so that nothing it throws can reach the session or the second phase.</summary>
internal static class RemoteResultCallback
{
    public static void Invoke<T>(Action<T>? callback, T result)
    {
        if (callback is null)
        {
            return;
        }

        try
        {
            callback(result);
        }
        catch
        {
            // The caller publishes; a failure there must not end the session or skip the probes.
        }
    }
}
