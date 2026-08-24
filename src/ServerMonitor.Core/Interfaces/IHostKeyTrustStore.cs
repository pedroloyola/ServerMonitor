using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Interfaces;

public interface IHostKeyTrustStore
{
    Task<TrustedHostKey?> GetAsync(
        SshEndpoint endpoint,
        CancellationToken cancellationToken = default);

    Task TrustAsync(
        SshEndpoint endpoint,
        HostKeyIdentity identity,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(
        SshEndpoint endpoint,
        CancellationToken cancellationToken = default);
}
