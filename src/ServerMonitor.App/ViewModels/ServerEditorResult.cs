using ServerMonitor.Core.Models;

namespace ServerMonitor.App.ViewModels;

public sealed class ServerEditorResult : IDisposable
{
    public ServerProfileInput Profile { get; init; } = null!;

    public SshConnectionResult? ConnectionResult { get; init; }

    public void Dispose() => Profile.CredentialChange.Secret?.Dispose();
}
