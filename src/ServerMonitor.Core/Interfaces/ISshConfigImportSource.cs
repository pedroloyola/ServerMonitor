using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.Core.Interfaces;

/// <summary>
/// Read-only access to the user's OpenSSH client configuration for the editor's import action.
/// Implementations never write the file and never open private key files.
/// </summary>
public interface ISshConfigImportSource
{
    Task<SshConfigImportResult> LoadAsync(CancellationToken cancellationToken = default);
}
