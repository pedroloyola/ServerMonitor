using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Discovery;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// REL.0B (Vigil L-1, Prism N-5): a user-started search ends by itself. With nothing visible it ends as NothingFound
/// at <see cref="DiscoveryOptions.NothingFoundTimeout"/>; with results it keeps listening until
/// <see cref="DiscoveryOptions.MaxSearchDuration"/>. The real service, a fake browser (no socket) and fake time.
/// </summary>
public sealed class DiscoverySearchBoundsTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-14T10:00:00Z");

    [Fact]
    public async Task Nothing_visible_ends_the_search_at_the_nothing_found_timeout()
    {
        var (service, browser, time) = Create();
        await using var _ = service;
        DateTimeOffset? endedAt = null;
        service.IsSearchingChanged += (_, _) => { if (!service.IsSearching) { endedAt ??= time.GetUtcNow(); } };
        await service.StartSearchAsync();

        // The stop flips IsSearching before it closes the browser (on another thread): wait for the counted stop too.
        await AdvanceUntilAsync(time, () => !service.IsSearching && browser.StopCount >= 1);

        Assert.Equal(DiscoverySearchEnd.NothingFound, service.LastSearchEnd);
        Assert.Equal(1, browser.StopCount);
        Assert.Equal(0, browser.FoundSubscriberCount);
        Assert.NotNull(endedAt);
        Assert.InRange(endedAt.Value - Start, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(50)); // lower bound is the contract; upper is a CI-lag tolerance
    }

    [Fact]
    public async Task Results_keep_the_search_open_past_the_nothing_found_timeout_until_the_hard_limit()
    {
        var (service, browser, time) = Create(maxSearch: TimeSpan.FromMinutes(2));
        await using var _ = service;
        DateTimeOffset? endedAt = null;
        service.IsSearchingChanged += (_, _) => { if (!service.IsSearching) { endedAt ??= time.GetUtcNow(); } };
        await service.StartSearchAsync();
        browser.EmitFound(Observation("Mac Studio", time));

        await AdvanceUntilAsync(time, () => time.GetUtcNow() - Start >= TimeSpan.FromSeconds(60));
        Assert.True(service.IsSearching);
        Assert.Single(service.GetDiscovered());

        // The stop flips IsSearching before it closes the browser (on another thread): wait for the counted stop too.
        await AdvanceUntilAsync(time, () => !service.IsSearching && browser.StopCount >= 1);
        Assert.Equal(DiscoverySearchEnd.TimeLimit, service.LastSearchEnd);
        Assert.InRange(endedAt!.Value - Start, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(10));
        Assert.Empty(service.GetDiscovered());
        Assert.Equal(1, browser.StopCount);
    }

    /// <summary>N-r2-2: a result that appeared and expired before 40 s is not "nothing found"; the search runs to its limit.</summary>
    [Fact]
    public async Task A_result_that_appeared_and_expired_is_not_nothing_found()
    {
        var (service, browser, time) = Create(maxSearch: TimeSpan.FromMinutes(2), expiry: TimeSpan.FromSeconds(10));
        await using var _ = service;
        await service.StartSearchAsync();
        browser.EmitFound(Observation("Mac Studio", time));
        await AdvanceUntilAsync(time, () => service.GetDiscovered().Count == 1);

        // Expired at ~10 s, nothing visible since; at 60 s it is still searching (not NothingFound at 40 s).
        await AdvanceUntilAsync(time, () => time.GetUtcNow() - Start >= TimeSpan.FromSeconds(60));
        Assert.Empty(service.GetDiscovered());
        Assert.True(service.IsSearching);

        await AdvanceUntilAsync(time, () => !service.IsSearching);
        Assert.Equal(DiscoverySearchEnd.TimeLimit, service.LastSearchEnd);
    }

    /// <summary>
    /// REL.0D O-8: with the PRODUCTION options, a server found once (the real browser raises nothing for its unchanged
    /// re-announcements) stays listed on every sweep until the 10 min limit ends the search, which then clears it and
    /// closes the listener.
    /// </summary>
    [Fact]
    public async Task With_the_default_options_a_result_found_once_stays_listed_until_the_time_limit()
    {
        var browser = new FakeMdnsServiceBrowser();
        var time = new FakeTimeProvider(Start);
        var service = new ServerDiscoveryService(browser, new GatedIgnoredDeviceStore(), NullLogger<ServerDiscoveryService>.Instance, time,
            DiscoveryOptions.Default);
        await using var _ = service;
        DateTimeOffset? endedAt = null;
        service.IsSearchingChanged += (_, _) => { if (!service.IsSearching) { endedAt ??= time.GetUtcNow(); } };
        await service.StartSearchAsync();
        browser.EmitFound(Observation("Mac Studio", time));

        // Bounded by the clock, not IsSearching: the automatic stop clears the list before it flips IsSearching.
        while (time.GetUtcNow() - Start < DiscoveryOptions.Default.MaxSearchDuration)
        {
            Assert.True(service.IsSearching);
            Assert.Single(service.GetDiscovered());
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(2);
        }

        // The stop flips IsSearching before it closes the browser (on another thread): wait for the counted stop too.
        await AdvanceUntilAsync(time, () => !service.IsSearching && browser.StopCount >= browser.StartCount);
        Assert.Equal(DiscoverySearchEnd.TimeLimit, service.LastSearchEnd);
        Assert.InRange(endedAt!.Value - Start, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(60));
        Assert.Empty(service.GetDiscovered());
        Assert.Equal(browser.StartCount, browser.StopCount);
        Assert.Equal(0, browser.FoundSubscriberCount);
        Assert.Equal(0, browser.UpdatedSubscriberCount);
        Assert.Equal(0, browser.RemovedSubscriberCount);
    }

    [Fact]
    public async Task A_manual_stop_wins_and_a_new_search_gets_a_fresh_bound()
    {
        var (service, browser, time) = Create();
        await using var _ = service;
        await service.StartSearchAsync();
        await AdvanceUntilAsync(time, () => time.GetUtcNow() - Start >= TimeSpan.FromSeconds(30));
        await service.StopSearchAsync();
        Assert.Equal(DiscoverySearchEnd.Stopped, service.LastSearchEnd);

        var restartedAt = time.GetUtcNow();
        await service.StartSearchAsync();
        Assert.Equal(DiscoverySearchEnd.None, service.LastSearchEnd);
        await AdvanceUntilAsync(time, () => time.GetUtcNow() - restartedAt >= TimeSpan.FromSeconds(20));
        // 50 s after the FIRST start, but only 20 s into this one: still searching.
        Assert.True(service.IsSearching);
        Assert.Equal(1, browser.StopCount);

        // The stop flips IsSearching before it closes the browser (on another thread): wait for the counted stop too.
        await AdvanceUntilAsync(time, () => !service.IsSearching && browser.StopCount >= 2);
        Assert.Equal(DiscoverySearchEnd.NothingFound, service.LastSearchEnd);
        Assert.Equal(2, browser.StopCount);
    }

    private static (ServerDiscoveryService Service, FakeMdnsServiceBrowser Browser, FakeTimeProvider Time) Create(
        TimeSpan? maxSearch = null, TimeSpan? expiry = null)
    {
        var browser = new FakeMdnsServiceBrowser();
        var time = new FakeTimeProvider(Start);
        var service = new ServerDiscoveryService(browser, new GatedIgnoredDeviceStore(), NullLogger<ServerDiscoveryService>.Instance, time,
            new DiscoveryOptions
            {
                // Results must not expire under the hard-limit test; the bounds themselves are the defaults' shape.
                ExpiryWindow = expiry ?? TimeSpan.FromDays(1),
                NothingFoundTimeout = TimeSpan.FromSeconds(40),
                MaxSearchDuration = maxSearch ?? DiscoveryOptions.Default.MaxSearchDuration
            });
        return (service, browser, time);
    }

    private static DiscoveryObservation Observation(string instance, TimeProvider time) =>
        DiscoveryInputPolicy.TryCreateObservation(instance, "_ssh._tcp", "local.", "mac-studio.local", 22,
            [IPAddress.Parse("192.168.1.20")], "nic-a", time.GetUtcNow())!;

    private static async Task AdvanceUntilAsync(FakeTimeProvider time, Func<bool> condition, int maxSteps = 3000)
    {
        for (var step = 0; step < maxSteps && !condition(); step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(2);
        }

        Assert.True(condition(), "condition not reached");
    }

    [Fact]
    public void Defaults_are_the_documented_bounds()
    {
        Assert.Equal(TimeSpan.FromSeconds(40), DiscoveryOptions.Default.NothingFoundTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), DiscoveryOptions.Default.MaxSearchDuration);
    }
}
