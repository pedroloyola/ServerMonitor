using ServerMonitor.Infrastructure.Discovery;

namespace ServerMonitor.Infrastructure.Tests.Discovery;

public sealed class MdnsServiceBrowserOptionsTests
{
    [Fact]
    public void Default_BrowsesOnlySshEveryThirtySeconds()
    {
        var options = MdnsServiceBrowserOptions.Default;

        Assert.Equal("_ssh._tcp", options.ServiceType);
        Assert.Equal(TimeSpan.FromSeconds(30), options.QueryInterval);
        Assert.Equal(30_000, options.ResolveQueryIntervalMilliseconds());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4.999)]
    public void TooShortOrNonPositiveInterval_ClampsToFiveSeconds(double seconds)
    {
        var options = new MdnsServiceBrowserOptions
        {
            QueryInterval = TimeSpan.FromSeconds(seconds)
        };

        Assert.Equal(5_000, options.ResolveQueryIntervalMilliseconds());
    }

    [Fact]
    public void TooLongInterval_ClampsToFiveMinutes()
    {
        var options = new MdnsServiceBrowserOptions
        {
            QueryInterval = TimeSpan.FromDays(1)
        };

        Assert.Equal(300_000, options.ResolveQueryIntervalMilliseconds());
    }

    [Fact]
    public void BoundaryIntervals_ArePreserved()
    {
        Assert.Equal(5_000, new MdnsServiceBrowserOptions
        {
            QueryInterval = MdnsServiceBrowserOptions.MinQueryInterval
        }.ResolveQueryIntervalMilliseconds());
        Assert.Equal(300_000, new MdnsServiceBrowserOptions
        {
            QueryInterval = MdnsServiceBrowserOptions.MaxQueryInterval
        }.ResolveQueryIntervalMilliseconds());
    }
}
