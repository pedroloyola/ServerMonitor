using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Services;

/// <summary>
/// Inert default <see cref="IServerDiscoveryService"/>: nothing is ever suggested, ignoring is a no-op.
/// <para>
/// Registered as the common-area default alongside the other inert defaults, closing the one gap M14.0
/// found: Discovery was the only one of the four already-isolated capabilities without one. Both
/// <c>DashboardViewModel</c> and <c>SettingsViewModel</c> require this service, and today EVERY
/// composition — the real one and all seven Debug QA harnesses — registers its own later, so this default
/// is always overridden and changes no observed behaviour. It exists so a composition that omits the
/// Discovery module degrades to "no suggestions" instead of failing to build the container.
/// </para>
/// <para>
/// <see cref="DiscoveredChanged"/> is never raised. That is the honest inert answer: the suggestion set is
/// permanently empty, so it never materially changes.
/// </para>
/// </summary>
public sealed class NullServerDiscoveryService : IServerDiscoveryService
{
    // Never raised: there is nothing to discover, so the visible set never materially changes. The
    // accessors exist to satisfy the contract without holding subscribers alive.
    public event EventHandler? DiscoveredChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<DiscoveredService> GetDiscovered() => [];

    public Task IgnoreAsync(ServiceInstanceIdentity identity, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ResetIgnoredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
