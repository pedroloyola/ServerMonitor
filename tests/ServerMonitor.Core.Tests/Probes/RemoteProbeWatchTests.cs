using ServerMonitor.Core.Probes;

namespace ServerMonitor.Core.Tests.Probes;

public sealed class RemoteProbeWatchTests
{
    private static readonly Guid ServerA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ServerB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void A_watch_covers_its_server_and_probe_only()
    {
        var watch = new RemoteProbeWatch();

        using var held = watch.Watch(ServerA, "page");

        Assert.True(watch.IsWatched(ServerA, "page"));
        Assert.False(watch.IsWatched(ServerB, "page"));
        Assert.False(watch.IsWatched(ServerA, "other"));
    }

    [Fact]
    public void Watches_are_counted_until_every_holder_lets_go()
    {
        var watch = new RemoteProbeWatch();
        var first = watch.Watch(ServerA, "page");
        var second = watch.Watch(ServerA, "page");

        first.Dispose();
        Assert.True(watch.IsWatched(ServerA, "page"));
        second.Dispose();
        Assert.False(watch.IsWatched(ServerA, "page"));
    }

    [Fact]
    public void Disposing_a_watch_twice_counts_once()
    {
        var watch = new RemoteProbeWatch();
        var first = watch.Watch(ServerA, "page");
        using var second = watch.Watch(ServerA, "page");

        first.Dispose();
        first.Dispose();

        Assert.True(watch.IsWatched(ServerA, "page"));
    }

    [Fact]
    public void An_empty_probe_id_is_refused()
    {
        var watch = new RemoteProbeWatch();

        Assert.Throws<ArgumentException>(() => watch.Watch(ServerA, ""));
        Assert.Throws<ArgumentNullException>(() => watch.Watch(ServerA, null!));
    }
}
