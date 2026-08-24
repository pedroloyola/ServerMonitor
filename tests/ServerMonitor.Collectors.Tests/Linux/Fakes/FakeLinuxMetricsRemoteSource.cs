using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Infrastructure.Collectors.Linux;

namespace ServerMonitor.Collectors.Tests.Linux.Fakes;

internal sealed class FakeLinuxMetricsRemoteSource : ILinuxMetricsRemoteSource
{
    public LinuxMetricsRemoteResult Result { get; set; } = new()
    {
        ConnectionResult = new SshConnectionResult
        {
            State = ServerConnectionState.Connected
        }
    };

    public Exception? ExceptionToThrow { get; set; }

    public int CallCount { get; private set; }

    public Server? LastServer { get; private set; }

    public TimeSpan? LastCpuSampleInterval { get; private set; }

    public TimeSpan? LastTimeout { get; private set; }

    public CancellationToken LastCancellationToken { get; private set; }

    public Task<LinuxMetricsRemoteResult> CollectAsync(
        Server server,
        TimeSpan cpuSampleInterval,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastServer = server;
        LastCpuSampleInterval = cpuSampleInterval;
        LastTimeout = timeout;
        LastCancellationToken = cancellationToken;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        return Task.FromResult(Result);
    }
}
