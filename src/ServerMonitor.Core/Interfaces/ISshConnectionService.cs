using ServerMonitor.Core.Models;

namespace ServerMonitor.Core.Interfaces;

public interface ISshConnectionService
{
    Task<SshConnectionResult> ConnectAsync(
        SshConnectionRequest request,
        CancellationToken cancellationToken = default);

    Task<SshConnectionResult> TestConnectionAsync(
        SshConnectionRequest request,
        CancellationToken cancellationToken = default);

    Task<SshConnectionResult> DetectOperatingSystemAsync(
        SshConnectionRequest request,
        CancellationToken cancellationToken = default);
}
