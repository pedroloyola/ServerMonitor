using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Discovery;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// REL.0B r2 (Cortex addendum, Vigil NIT-A): a browser whose Stop() throws must never leave the UDP 5353 listener open
/// while the service reports "stopped". The service-side stop (C-1 order: clear, then signal) still completes; the
/// browser stop is retried by a bounded loop on the injected clock, by any later stop, and before any new start.
/// The real service; the fake browser fails on demand; nothing opens a socket.
/// </summary>
public sealed class DiscoveryStopFailureTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-14T12:00:00Z");

    [Fact]
    public async Task A_failed_browser_stop_is_retried_until_the_listener_is_closed()
    {
        var (service, browser, time) = Create();
        await using var _ = service;
        await service.StartSearchAsync();
        browser.StopFailures = 2;

        await service.StopSearchAsync(); // must not throw

        // The service-side stop is final at once (C-1): nothing searching, nothing suggested, callbacks detached.
        Assert.False(service.IsSearching);
        Assert.Equal(DiscoverySearchEnd.Stopped, service.LastSearchEnd);
        Assert.Empty(service.GetDiscovered());
        Assert.Equal(0, browser.FoundSubscriberCount);
        Assert.Equal(0, browser.StopCount);

        await AdvanceUntilAsync(time, () => browser.StopCount == 1);
        Assert.Equal(["start", "stop-failed", "stop-failed", "stop"], browser.Calls);
    }

    [Fact]
    public async Task A_later_stop_retries_a_failed_browser_stop()
    {
        var (service, browser, _) = Create();
        await using var __ = service;
        await service.StartSearchAsync();
        browser.StopFailures = 1;
        await service.StopSearchAsync();
        Assert.Equal(0, browser.StopCount);

        await service.StopSearchAsync(); // e.g. the hide after a failed auto-stop, or exit

        Assert.Equal(1, browser.StopCount);
        Assert.Equal(["start", "stop-failed", "stop"], browser.Calls);
    }

    [Fact]
    public async Task A_new_search_closes_the_old_session_before_opening_another()
    {
        var (service, browser, _) = Create();
        await using var __ = service;
        await service.StartSearchAsync();
        browser.StopFailures = 1;
        await service.StopSearchAsync();

        await service.StartSearchAsync();

        Assert.True(service.IsSearching);
        Assert.Equal(["start", "stop-failed", "stop", "start"], browser.Calls);
    }

    [Fact]
    public async Task A_new_search_is_refused_while_the_old_session_still_cannot_be_stopped()
    {
        var (service, browser, _) = Create();
        await using var __ = service;
        await service.StartSearchAsync();
        browser.StopFailures = 3; // the stop, the pre-start retry and the rollback's retry all fail
        await service.StopSearchAsync();

        await service.StartSearchAsync();

        // Never two listeners: the start fails closed and is reported; the old session is still pending a stop.
        Assert.False(service.IsSearching);
        Assert.Equal(DiscoverySearchEnd.Failed, service.LastSearchEnd);
        Assert.Equal(1, browser.StartCount);
        Assert.Equal(0, browser.StopCount);
        Assert.Equal(["start", "stop-failed", "stop-failed", "stop-failed"], browser.Calls);
    }

    [Fact]
    public async Task An_automatic_stop_whose_browser_stop_fails_still_ends_with_the_listener_closed()
    {
        var (service, browser, time) = Create();
        await using var _ = service;
        await service.StartSearchAsync();
        browser.StopFailures = 3;

        await AdvanceUntilAsync(time, () => !service.IsSearching);
        Assert.Equal(DiscoverySearchEnd.NothingFound, service.LastSearchEnd);
        Assert.Equal(0, browser.FoundSubscriberCount);

        await AdvanceUntilAsync(time, () => browser.StopCount == 1);
        Assert.Equal(4, browser.StopAttempts);
    }

    [Fact]
    public async Task Dispose_retries_a_pending_browser_stop()
    {
        var (service, browser, _) = Create();
        await service.StartSearchAsync();
        browser.StopFailures = 1;
        await service.StopSearchAsync();

        await service.DisposeAsync();

        Assert.Equal(1, browser.StopCount);
    }

    private static (ServerDiscoveryService Service, FakeMdnsServiceBrowser Browser, FakeTimeProvider Time) Create()
    {
        var browser = new FakeMdnsServiceBrowser();
        var time = new FakeTimeProvider(Start);
        var service = new ServerDiscoveryService(browser, new GatedIgnoredDeviceStore(), NullLogger<ServerDiscoveryService>.Instance, time,
            new DiscoveryOptions { NothingFoundTimeout = TimeSpan.FromSeconds(40) });
        return (service, browser, time);
    }

    private static async Task AdvanceUntilAsync(FakeTimeProvider time, Func<bool> condition, int maxSteps = 3000)
    {
        for (var step = 0; step < maxSteps && !condition(); step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(2);
        }

        Assert.True(condition(), "condition not reached");
    }
}
