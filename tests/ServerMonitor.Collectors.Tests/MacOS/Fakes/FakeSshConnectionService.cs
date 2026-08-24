using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.Collectors.Tests.MacOS.Fakes;

/// <summary>
/// Minimal <see cref="ISshConnectionService"/> for router tests: only
/// DetectOperatingSystemAsync is exercised; the other members are not expected
/// to be called and fail loudly if they are.
/// </summary>
internal sealed class FakeSshConnectionService : ISshConnectionService
{
    public SshConnectionResult DetectionResult { get; set; } = new()
    {
        State = ServerConnectionState.Connected,
        DetectedOperatingSystem = ServerOperatingSystem.Linux
    };

    public Exception? DetectionException { get; set; }

    public int DetectCallCount { get; private set; }

    public Task<SshConnectionResult> ConnectAsync(
        SshConnectionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("ConnectAsync should not be called by the router.");

    public Task<SshConnectionResult> TestConnectionAsync(
        SshConnectionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("TestConnectionAsync should not be called by the router.");

    public Task<SshConnectionResult> DetectOperatingSystemAsync(
        SshConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        DetectCallCount++;

        if (DetectionException is not null)
        {
            throw DetectionException;
        }

        return Task.FromResult(DetectionResult);
    }
}
