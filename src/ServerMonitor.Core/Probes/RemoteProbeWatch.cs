namespace ServerMonitor.Core.Probes;

/// <summary>
/// Says which watched-only probes (<see cref="IRemoteProbe.RunsOnlyWhileWatched"/>) may run on which server now. A page
/// that shows such a probe's result holds a <see cref="Watch"/> for its server while it is on screen and disposes it when
/// it leaves, so the probe runs on the polls in between and on no other: never in the background.
/// <para>
/// Watches are counted: two holders of the same server and probe keep it running until both let go. Disposing a watch
/// more than once counts once. Nothing here is stored, logged or sent anywhere.
/// </para>
/// </summary>
public sealed class RemoteProbeWatch
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid ServerId, string ProbeId), int> _holders = [];

    /// <summary>Starts watching <paramref name="probeId"/> on <paramref name="serverId"/> until the result is disposed.</summary>
    public IDisposable Watch(Guid serverId, string probeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(probeId);
        var key = (serverId, probeId);
        lock (_gate)
        {
            _holders[key] = _holders.GetValueOrDefault(key) + 1;
        }

        return new Lease(this, key);
    }

    /// <summary>True while at least one watch of <paramref name="probeId"/> on <paramref name="serverId"/> is held.</summary>
    public bool IsWatched(Guid serverId, string probeId)
    {
        lock (_gate)
        {
            return _holders.ContainsKey((serverId, probeId));
        }
    }

    private void Release((Guid ServerId, string ProbeId) key)
    {
        lock (_gate)
        {
            if (!_holders.TryGetValue(key, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                _holders.Remove(key);
            }
            else
            {
                _holders[key] = count - 1;
            }
        }
    }

    private sealed class Lease(RemoteProbeWatch owner, (Guid ServerId, string ProbeId) key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(key);
            }
        }
    }
}
