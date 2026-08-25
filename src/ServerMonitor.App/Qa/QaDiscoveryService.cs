using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY in-memory <see cref="IServerDiscoveryService"/>. It serves a fixed seed of suggestions
/// and honours Ignore / ResetIgnored entirely in memory — no mDNS browser, no persistence, no
/// network and no SSH. Ignoring a seed drops it from the visible set (two → one → zero) and
/// resetting brings every seed back, so the whole discovery UX can be driven deterministically.
/// Used for both --qa-discovery (with the seed below) and --qa-health (with an empty seed, so the
/// dashboard still resolves without any real discovery running).
/// </summary>
internal sealed class QaDiscoveryService : IServerDiscoveryService
{
    private readonly object _sync = new();
    private readonly List<DiscoveredService> _seed;
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);

    public QaDiscoveryService(IReadOnlyList<DiscoveredService> seed) => _seed = [.. seed];

    public event EventHandler? DiscoveredChanged;

    public IReadOnlyList<DiscoveredService> GetDiscovered()
    {
        lock (_sync)
        {
            return _seed
                .Where(service => !_ignored.Contains(service.Identity.StableHash))
                .OrderBy(service => service.FirstSeenAt)
                .ThenBy(service => service.DisplayName, StringComparer.Ordinal)
                .ToList();
        }
    }

    public Task IgnoreAsync(ServiceInstanceIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        bool changed;
        lock (_sync)
        {
            changed = _ignored.Add(identity.StableHash);
        }

        if (changed)
        {
            DiscoveredChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    public Task ResetIgnoredAsync(CancellationToken cancellationToken = default)
    {
        bool changed;
        lock (_sync)
        {
            changed = _ignored.Count > 0;
            _ignored.Clear();
        }

        if (changed)
        {
            DiscoveredChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }
}
