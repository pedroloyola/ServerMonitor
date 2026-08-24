using ServerMonitor.Core.Security;

namespace ServerMonitor.Core.Interfaces;

public interface IServerCredentialStore
{
    Task WriteAsync(
        CredentialReference reference,
        SecretValue secret,
        CancellationToken cancellationToken = default);

    Task<SecretValue?> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default);
}
