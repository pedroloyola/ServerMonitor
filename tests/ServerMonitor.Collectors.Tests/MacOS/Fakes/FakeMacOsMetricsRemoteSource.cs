using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Collectors.MacOS;

namespace ServerMonitor.Collectors.Tests.MacOS.Fakes;

internal sealed class FakeMacOsMetricsRemoteSource : IMacOsMetricsRemoteSource
{
    public MacOsMetricsRemoteResult Result { get; set; } = new()
    {
        ConnectionResult = new SshConnectionResult
        {
            State = ServerConnectionState.Connected
        }
    };

    public Exception? ExceptionToThrow { get; set; }

    public int CallCount { get; private set; }

    public Server? LastServer { get; private set; }

    public TimeSpan? LastTimeout { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public Task<MacOsMetricsRemoteResult> CollectAsync(
        Server server,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastServer = server;
        LastTimeout = timeout;
        LastCancellationToken = cancellationToken;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(Result);
    }
}
