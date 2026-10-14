using ServerMonitor.App.Services;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY stand-in for <c>--qa-discovery</c> that mirrors the production lifecycle after REL.0B: nothing is
/// suggested and nothing is searching until the empty state's explicit "Search local network" runs
/// <see cref="StartSearchAsync"/>. Then it reports searching, reveals the seed after a short delay (so
/// the search indicator is visible first), and a stop (window to background) hides everything again. In memory
/// only - no mDNS browser, no socket, no persistence, no SSH. Excluded from Release.
/// </summary>
internal sealed class QaOnDemandDiscoveryService(IReadOnlyList<DiscoveredService> seed, TimeSpan? revealDelay = null)
    : IServerDiscoveryService, IServerDiscoveryActivity
{
    /// <summary>How long the QA search "looks" before the seed appears (the launch default).</summary>
    internal static readonly TimeSpan DefaultRevealDelay = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _revealDelay = revealDelay ?? DefaultRevealDelay;

    private readonly QaDiscoveryService _inner = new(seed);
    private readonly object _sync = new();
    private bool _searching;
    private bool _revealed;
    private long _generation;

    public event EventHandler? DiscoveredChanged;

    public event EventHandler? IsSearchingChanged;

    public bool IsSearching
    {
        get { lock (_sync) { return _searching; } }
    }

    /// <summary>How many times a search was really started (test seam for "exactly once").</summary>
    internal int StartCount { get; private set; }

    public IReadOnlyList<DiscoveredService> GetDiscovered()
    {
        lock (_sync)
        {
            if (!_revealed)
            {
                return [];
            }
        }

        return _inner.GetDiscovered();
    }

    public async Task IgnoreAsync(ServiceInstanceIdentity identity, CancellationToken cancellationToken = default)
    {
        await _inner.IgnoreAsync(identity, cancellationToken);
        DiscoveredChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ResetIgnoredAsync(CancellationToken cancellationToken = default)
    {
        await _inner.ResetIgnoredAsync(cancellationToken);
        DiscoveredChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task StartSearchAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        lock (_sync)
        {
            if (_searching)
            {
                return Task.CompletedTask;
            }

            _searching = true;
            StartCount++;
            generation = ++_generation;
        }

        IsSearchingChanged?.Invoke(this, EventArgs.Empty);
        _ = RevealAfterDelayAsync(generation);
        return Task.CompletedTask;
    }

    public Task StopSearchAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (!_searching)
            {
                return Task.CompletedTask;
            }

            _searching = false;
            _revealed = false;
            _generation++;
        }

        IsSearchingChanged?.Invoke(this, EventArgs.Empty);
        DiscoveredChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private async Task RevealAfterDelayAsync(long generation)
    {
        await Task.Delay(_revealDelay).ConfigureAwait(false);
        lock (_sync)
        {
            if (generation != _generation || !_searching)
            {
                return;
            }

            _revealed = true;
        }

        DiscoveredChanged?.Invoke(this, EventArgs.Empty);
    }
}
