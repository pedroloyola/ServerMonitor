using System.Collections.Concurrent;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Security;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY process-local credential store. Secrets live only for the lifetime of the QA process and never
/// reach the Windows Credential Manager. Each value is copied in and handed out as a fresh
/// <see cref="SecretValue"/>, so a caller disposing its copy cannot wipe the stored one.
/// </summary>
internal sealed class QaInMemoryCredentialStore : IServerCredentialStore
{
    private readonly ConcurrentDictionary<CredentialReference, char[]> _secrets = new();

    public Task WriteAsync(
        CredentialReference reference,
        SecretValue secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        cancellationToken.ThrowIfCancellationRequested();

        _secrets[reference] = secret.Reveal().ToArray();
        return Task.CompletedTask;
    }

    public Task<SecretValue?> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_secrets.TryGetValue(reference, out var characters)
            ? new SecretValue(characters)
            : null);
    }

    public Task<bool> DeleteAsync(
        CredentialReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_secrets.TryRemove(reference, out _));
    }
}
