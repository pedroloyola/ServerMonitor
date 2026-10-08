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

    // ---- UI.9 D-UI9-4: derived snapshot threshold = max(90 s, max_i staleAfterSeconds_i) --------------

    private static WidgetStateSnapshot Fleet(DateTimeOffset generatedAt, params int?[] staleAfter) => new()
    {
        SchemaVersion = WidgetSchema.CurrentVersion,
        GeneratedAtUtc = generatedAt,
        OverallHealth = WidgetHealth.Healthy,
        Servers = staleAfter.Select((s, i) => new WidgetServerState
        {
            Id = new Guid($"00000000-0000-0000-0000-{i + 1:D12}"),
            DisplayName = $"s{i}",
            Health = WidgetHealth.Healthy,
            LastUpdatedUtc = generatedAt,
            StaleAfterSeconds = s
        }).ToArray()
    };

    [Theory]
    [InlineData(new int[0], 90)]            // empty fleet → floor
    [InlineData(new[] { 20, 60 }, 90)]      // all below the floor
    [InlineData(new[] { 60, 120 }, 120)]
    [InlineData(new[] { 20, 600, 60 }, 600)] // the slowest server wins
    public void Derived_threshold_is_the_max_of_the_floor_and_every_server(int[] staleAfter, int expectedSeconds)
    {
        var snapshot = Fleet(Now, staleAfter.Select(s => (int?)s).ToArray());
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), WidgetFreshness.DeriveSnapshotThreshold(snapshot));
    }

    [Fact]
    public void Old_file_without_per_server_values_keeps_ninety_seconds()
    {
        Assert.Equal(WidgetFreshness.DefaultStaleThreshold,
            WidgetFreshness.DeriveSnapshotThreshold(Fleet(Now, null, null)));
    }

    [Fact]
    public void Derived_threshold_boundary_is_inclusive_on_the_providers_clock()
    {
        var atBound = WidgetReadResult.Available(Fleet(Now.AddSeconds(-600), 600));
        var pastBound = WidgetReadResult.Available(Fleet(Now.AddSeconds(-600).AddTicks(-1), 600));

        Assert.Equal(WidgetFreshnessState.Fresh, WidgetFreshness.Evaluate(atBound, Now));
        Assert.Equal(WidgetFreshnessState.Stale, WidgetFreshness.Evaluate(pastBound, Now));
    }

    [Fact]
    public void Explicit_threshold_overrides_the_derived_one()
    {
        var read = WidgetReadResult.Available(Fleet(Now.AddSeconds(-300), 600));
        Assert.Equal(WidgetFreshnessState.Stale, WidgetFreshness.Evaluate(read, Now, TimeSpan.FromSeconds(90)));
    }

    // ---- per-server freshness (D-UI9-5, V-RC-5) ------------------------------------------------------

    [Fact]
    public void A_server_is_never_fresher_than_a_stale_snapshot()
    {
        var server = Fleet(Now, 600).Servers[0];
        Assert.False(WidgetFreshness.IsServerFresh(server, WidgetFreshnessState.Stale, TimeSpan.FromSeconds(600), Now));
    }

    [Fact]
    public void A_server_never_read_is_never_fresh()
    {
        var server = Fleet(Now, 600).Servers[0] with { LastUpdatedUtc = null };
        Assert.False(WidgetFreshness.IsServerFresh(server, WidgetFreshnessState.Fresh, TimeSpan.FromSeconds(600), Now));
    }

    [Fact]
    public void A_server_uses_its_own_threshold_and_falls_back_to_the_snapshot_one()
    {
        var own = Fleet(Now.AddSeconds(-61), 60).Servers[0];
        Assert.False(WidgetFreshness.IsServerFresh(own, WidgetFreshnessState.Fresh, TimeSpan.FromSeconds(600), Now));

        var fallback = own with { StaleAfterSeconds = null };
        Assert.True(WidgetFreshness.IsServerFresh(fallback, WidgetFreshnessState.Fresh, TimeSpan.FromSeconds(90), Now));
    }

    [Fact]
    public void Unavailable_read_is_unavailable()
    {
        var read = WidgetReadResult.Unavailable(WidgetReadUnavailableReason.Missing);
        Assert.Equal(WidgetFreshnessState.Unavailable, WidgetFreshness.Evaluate(read, Now));
    }
}
