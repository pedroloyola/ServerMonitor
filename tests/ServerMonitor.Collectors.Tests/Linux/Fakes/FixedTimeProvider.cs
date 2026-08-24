namespace ServerMonitor.Collectors.Tests.Linux.Fakes;

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
