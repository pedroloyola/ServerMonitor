using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Interfaces;

/// <summary>
/// Host-key trust for a target reached through a jump host, keyed by <see cref="SshRoute"/>. Kept apart
/// from <see cref="IHostKeyTrustStore"/>: a direct lookup never sees a routed entry and a routed lookup
/// never sees a direct one. Same conflict semantics and the same fail-closed loading as the direct store.
/// </summary>
public interface IRoutedHostKeyTrustStore
{
    Task<TrustedRoutedHostKey?> GetAsync(
        SshRoute route,
        CancellationToken cancellationToken = default);

    Task TrustAsync(
        SshRoute route,
        HostKeyIdentity identity,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(
        SshRoute route,
        CancellationToken cancellationToken = default);
}
