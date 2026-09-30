using ServerMonitor.Core.Backup;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Security;

namespace ServerMonitor.Infrastructure.Security;

/// <summary>
/// The <see cref="IServerCredentialStore"/> every ordinary caller receives (M14.6 §5.5): writes and deletes
/// take a configuration-gate lease, so no credential changes while a restore holds the gate or after it
/// committed. Reads are not gated. The restore engine uses the raw store through
/// <see cref="UngatedCredentialStore"/> instead.
/// </summary>
public sealed class GatedCredentialStore(IServerCredentialStore inner, IConfigurationWriteGate writeGate)
    : IServerCredentialStore
{
    private readonly IServerCredentialStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IConfigurationWriteGate _writeGate = writeGate ?? throw new ArgumentNullException(nameof(writeGate));

    public async Task WriteAsync(
        CredentialReference reference,
        SecretValue secret,
        CancellationToken cancellationToken = default)
    {
        using var lease = _writeGate.EnterWrite();
        await _inner.WriteAsync(reference, secret, cancellationToken).ConfigureAwait(false);
    }

    public Task<SecretValue?> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(reference, cancellationToken);

    public async Task<bool> DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        using var lease = _writeGate.EnterWrite();
        return await _inner.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The raw credential store (Windows Credential Manager, or the QA in-memory store) as a distinct DI service,
/// so only the backup engine and startup recovery can reach it; everything else gets
/// <see cref="GatedCredentialStore"/>.
/// </summary>
public sealed class UngatedCredentialStore(IServerCredentialStore store)
{
    public IServerCredentialStore Store { get; } = store ?? throw new ArgumentNullException(nameof(store));
}
