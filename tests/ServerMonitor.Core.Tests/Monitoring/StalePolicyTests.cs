using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.Core.Tests.Monitoring;

public sealed class StalePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Never_succeeded_is_not_stale()
    {
        Assert.False(StalePolicy.IsStale(lastSuccessAt: null, Now, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Recent_success_is_not_stale()
    {
        var interval = TimeSpan.FromSeconds(30);
        var lastSuccess = Now - TimeSpan.FromSeconds(40); // within 2x = 60s
        Assert.False(StalePolicy.IsStale(lastSuccess, Now, interval));
    }

    [Fact]
    public void Beyond_twice_interval_is_stale()
    {
        var interval = TimeSpan.FromSeconds(30);
        var lastSuccess = Now - TimeSpan.FromSeconds(61); // just past 2x = 60s
        Assert.True(StalePolicy.IsStale(lastSuccess, Now, interval));
    }

    [Fact]
    public void Short_interval_uses_floor_to_avoid_flapping()
    {
        // 10s interval -> 2x = 20s, which equals the floor; 15s must not be stale yet.
        var interval = TimeSpan.FromSeconds(10);
        Assert.False(StalePolicy.IsStale(Now - TimeSpan.FromSeconds(15), Now, interval));
        Assert.True(StalePolicy.IsStale(Now - TimeSpan.FromSeconds(25), Now, interval));
    }

    [Fact]
    public void StaleAfter_is_twice_interval_with_floor()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), StalePolicy.StaleAfter(TimeSpan.FromSeconds(5)));   // floor
        Assert.Equal(TimeSpan.FromSeconds(60), StalePolicy.StaleAfter(TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(600), StalePolicy.StaleAfter(TimeSpan.FromSeconds(300)));
    }
}
