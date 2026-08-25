using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.Core.Tests.Monitoring;

public sealed class RefreshIntervalPolicyTests
{
    [Theory]
    [InlineData(10, 10)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(300, 300)]
    public void Supported_values_are_preserved(int input, int expected) =>
        Assert.Equal(expected, RefreshIntervalPolicy.Normalize(input));

    [Theory]
    [InlineData(0, 30)]      // absent / unset -> default (migration of old servers.json)
    [InlineData(-5, 30)]     // invalid -> default
    [InlineData(3, 10)]      // below minimum -> clamp to 10, never faster
    [InlineData(45, 60)]     // equidistant 30/60 -> tie prefers the slower (safer) 60
    [InlineData(50, 60)]     // nearer to 60
    [InlineData(120, 60)]    // nearest supported below 300
    [InlineData(1000, 300)]  // above max -> slowest supported
    public void Out_of_catalog_values_snap_safely(int input, int expected) =>
        Assert.Equal(expected, RefreshIntervalPolicy.Normalize(input));

    [Fact]
    public void Never_returns_below_minimum()
    {
        foreach (var seconds in new[] { -100, 0, 1, 5, 9 })
        {
            Assert.True(RefreshIntervalPolicy.Normalize(seconds) >= RefreshIntervalPolicy.MinimumSeconds);
        }
    }

    [Fact]
    public void ToInterval_uses_normalized_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RefreshIntervalPolicy.ToInterval(0));
        Assert.Equal(TimeSpan.FromSeconds(10), RefreshIntervalPolicy.ToInterval(10));
    }
}
