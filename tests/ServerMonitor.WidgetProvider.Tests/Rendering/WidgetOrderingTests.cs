using ServerMonitor.WidgetContract;
using ServerMonitor.WidgetProvider.Rendering;

namespace ServerMonitor.WidgetProvider.Tests.Rendering;

public sealed class WidgetOrderingTests
{
    private static WidgetServerState S(string name, WidgetHealth health, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        DisplayName = name,
        Health = health,
        LastUpdatedUtc = null
    };

    [Fact]
    public void Problems_are_ordered_before_healthy()
    {
        var input = new[]
        {
            S("h", WidgetHealth.Healthy),
            S("u", WidgetHealth.Unknown),
            S("w", WidgetHealth.Warning),
            S("c", WidgetHealth.Critical),
            S("o", WidgetHealth.Offline)
        };

        var ordered = WidgetOrdering.ForDisplay(input).Select(s => s.Health).ToArray();

        Assert.Equal(new[]
        {
            WidgetHealth.Offline, WidgetHealth.Critical, WidgetHealth.Warning, WidgetHealth.Unknown, WidgetHealth.Healthy
        }, ordered);
    }

    [Fact]
    public void Equal_severity_and_name_break_ties_by_id_deterministically()
    {
        // Same severity AND same name → the id is the total, culture-independent tiebreak. Assert the
        // EXACT id sequence, and prove it is identical for a reversed input (so removing the id tiebreak
        // would fail this test).
        var id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var id3 = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var forward = new[] { S("Alpha", WidgetHealth.Warning, id1), S("Alpha", WidgetHealth.Warning, id2), S("Alpha", WidgetHealth.Warning, id3) };
        var reversed = new[] { S("Alpha", WidgetHealth.Warning, id3), S("Alpha", WidgetHealth.Warning, id2), S("Alpha", WidgetHealth.Warning, id1) };

        var expected = new[] { id1, id2, id3 };
        Assert.Equal(expected, WidgetOrdering.ForDisplay(forward).Select(s => s.Id));
        Assert.Equal(expected, WidgetOrdering.ForDisplay(reversed).Select(s => s.Id));
    }

    [Fact]
    public void Same_severity_orders_by_name_then_id()
    {
        var id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var input = new[]
        {
            S("Beta", WidgetHealth.Warning, id1),
            S("Alpha", WidgetHealth.Warning, id2),
            S("Alpha", WidgetHealth.Warning, id1)
        };

        var ordered = WidgetOrdering.ForDisplay(input).ToArray();

        // Alpha (id1) < Alpha (id2) < Beta.
        Assert.Equal(new[] { ("Alpha", id1), ("Alpha", id2), ("Beta", id1) },
            ordered.Select(s => (s.DisplayName, s.Id)).ToArray());
    }

    [Fact]
    public void Order_is_the_same_across_repeated_calls()
    {
        var input = new[]
        {
            S("z", WidgetHealth.Healthy), S("a", WidgetHealth.Critical), S("m", WidgetHealth.Warning)
        };

        var first = WidgetOrdering.ForDisplay(input).Select(s => s.DisplayName).ToArray();
        var second = WidgetOrdering.ForDisplay(input).Select(s => s.DisplayName).ToArray();
        Assert.Equal(first, second);
    }
}
