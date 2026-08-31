using ServerMonitor.WidgetContract;

namespace ServerMonitor.WidgetContract.Tests;

public sealed class WidgetHealthPrecedenceTests
{
    [Fact]
    public void Empty_fleet_is_Unknown()
    {
        Assert.Equal(WidgetHealth.Unknown, WidgetHealthPrecedence.Worst(Array.Empty<WidgetHealth>()));
    }

    [Fact]
    public void All_unknown_stays_Unknown()
    {
        Assert.Equal(WidgetHealth.Unknown,
            WidgetHealthPrecedence.Worst(new[] { WidgetHealth.Unknown, WidgetHealth.Unknown }));
    }

    // Mandated worked examples (§21, human decision 2026-08-30):
    // Offline > Critical > Warning > Unknown > Healthy.
    [Theory]
    [InlineData(WidgetHealth.Healthy, WidgetHealth.Healthy, WidgetHealth.Healthy)]   // all healthy -> Healthy
    [InlineData(WidgetHealth.Healthy, WidgetHealth.Unknown, WidgetHealth.Unknown)]   // Unknown outranks Healthy
    [InlineData(WidgetHealth.Healthy, WidgetHealth.Warning, WidgetHealth.Warning)]
    [InlineData(WidgetHealth.Unknown, WidgetHealth.Warning, WidgetHealth.Warning)]
    [InlineData(WidgetHealth.Critical, WidgetHealth.Unknown, WidgetHealth.Critical)]
    [InlineData(WidgetHealth.Offline, WidgetHealth.Critical, WidgetHealth.Offline)]
    [InlineData(WidgetHealth.Offline, WidgetHealth.Unknown, WidgetHealth.Offline)]
    public void Worst_of_pair(WidgetHealth a, WidgetHealth b, WidgetHealth expected)
    {
        Assert.Equal(expected, WidgetHealthPrecedence.Worst(new[] { a, b }));
        Assert.Equal(expected, WidgetHealthPrecedence.Worst(new[] { b, a })); // order independent
    }

    [Fact]
    public void Healthy_only_reported_when_nothing_worse_present()
    {
        Assert.Equal(WidgetHealth.Healthy,
            WidgetHealthPrecedence.Worst(new[] { WidgetHealth.Healthy, WidgetHealth.Healthy, WidgetHealth.Healthy }));
    }

    [Fact]
    public void Unknown_outranks_Healthy()
    {
        var healths = new[] { WidgetHealth.Healthy, WidgetHealth.Unknown, WidgetHealth.Healthy };
        Assert.Equal(WidgetHealth.Unknown, WidgetHealthPrecedence.Worst(healths));
    }

    [Theory]
    [InlineData(WidgetHealth.Healthy, 0)]
    [InlineData(WidgetHealth.Unknown, 1)]
    [InlineData(WidgetHealth.Warning, 2)]
    [InlineData(WidgetHealth.Critical, 3)]
    [InlineData(WidgetHealth.Offline, 4)]
    public void Rank_is_monotonic_and_explicit(WidgetHealth health, int expected)
    {
        Assert.Equal(expected, WidgetHealthPrecedence.Rank(health));
    }

    [Fact]
    public void Undefined_values_canonicalize_to_Unknown_and_stay_order_independent()
    {
        var bad = (WidgetHealth)99;
        Assert.Equal(WidgetHealth.Unknown, WidgetHealthPrecedence.Worst(new[] { bad }));
        Assert.Equal(WidgetHealth.Unknown, WidgetHealthPrecedence.Worst(new[] { bad, WidgetHealth.Unknown }));
        Assert.Equal(WidgetHealth.Unknown, WidgetHealthPrecedence.Worst(new[] { WidgetHealth.Unknown, bad }));
        // An undefined value never outranks a real problem, and the output is always a defined enum.
        Assert.Equal(WidgetHealth.Warning, WidgetHealthPrecedence.Worst(new[] { bad, WidgetHealth.Warning }));
        Assert.Equal(WidgetHealth.Healthy, WidgetHealthPrecedence.Worst(new[] { WidgetHealth.Healthy }));
    }

    [Fact]
    public void Worst_is_order_independent_across_a_mixed_fleet()
    {
        var forward = new[] { WidgetHealth.Healthy, WidgetHealth.Offline, WidgetHealth.Warning, WidgetHealth.Unknown };
        var reverse = new[] { WidgetHealth.Unknown, WidgetHealth.Warning, WidgetHealth.Offline, WidgetHealth.Healthy };
        Assert.Equal(WidgetHealthPrecedence.Worst(forward), WidgetHealthPrecedence.Worst(reverse));
        Assert.Equal(WidgetHealth.Offline, WidgetHealthPrecedence.Worst(forward));
    }
}
