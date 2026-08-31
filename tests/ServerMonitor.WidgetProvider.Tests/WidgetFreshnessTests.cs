using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Reading;

namespace ServerMonitor.WidgetProvider.Tests;

public sealed class WidgetFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static WidgetReadResult Available(DateTimeOffset generatedAt) =>
        WidgetReadResult.Available(new WidgetStateSnapshot
        {
            SchemaVersion = WidgetSchema.CurrentVersion,
            GeneratedAtUtc = generatedAt,
            OverallHealth = WidgetHealth.Healthy,
            Servers = Array.Empty<WidgetServerState>()
        });

    [Fact]
    public void Recent_snapshot_is_fresh()
    {
        var read = Available(Now.AddSeconds(-30));
        Assert.Equal(WidgetFreshnessState.Fresh, WidgetFreshness.Evaluate(read, Now));
    }

    [Fact]
    public void At_threshold_is_fresh()
    {
        var read = Available(Now - WidgetFreshness.DefaultStaleThreshold);
        Assert.Equal(WidgetFreshnessState.Fresh, WidgetFreshness.Evaluate(read, Now));
    }

    [Fact]
    public void Past_threshold_is_stale()
    {
        var read = Available(Now - WidgetFreshness.DefaultStaleThreshold - TimeSpan.FromSeconds(1));
        Assert.Equal(WidgetFreshnessState.Stale, WidgetFreshness.Evaluate(read, Now));
    }

    [Fact]
    public void Future_snapshot_is_fresh()
    {
        var read = Available(Now.AddSeconds(10));
        Assert.Equal(WidgetFreshnessState.Fresh, WidgetFreshness.Evaluate(read, Now));
    }

    [Fact]
    public void Unavailable_read_is_unavailable()
    {
        var read = WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Missing);
        Assert.Equal(WidgetFreshnessState.Unavailable, WidgetFreshness.Evaluate(read, Now));
    }
}
