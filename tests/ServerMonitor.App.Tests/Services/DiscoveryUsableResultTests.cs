using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// REL.0D O-7: only a USABLE result (one the user could still add) keeps a search past
/// <see cref="DiscoveryOptions.NothingFoundTimeout"/>. Servers already configured, ignored devices (before or during the
/// search) and duplicate announcements of those end the search at 40 s as NothingFound, where they used to keep the
/// listener open, with nothing to show, until the 10 min limit. The real service, a fake browser (no socket), fake time.
/// </summary>
public sealed class DiscoveryUsableResultTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-15T10:00:00Z");
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(10); // CI-lag tolerance; the lower bound is the contract

    [Fact]
    public async Task No_announcement_at_all_ends_as_nothing_found_at_40_seconds()
    {
        var w = new World();
        await using var _ = w.Service;
        await w.Service.StartSearchAsync(_ => true);

        await AdvanceUntilAsync(w.Time, () => !w.Service.IsSearching);

        w.AssertEndedAt(DiscoverySearchEnd.NothingFound, TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task Only_servers_already_configured_end_the_search_at_40_seconds()
    {
        var w = new World();
        await using var _ = w.Service;
        await w.Service.StartSearchAsync(IsNotConfigured);
        w.Browser.EmitFound(Observation("web-01", "web-01.local", "10.0.0.5", w.Time));
        w.Browser.EmitFound(Observation("db-01", "db-01.local", "10.0.0.6", w.Time));
        await AdvanceUntilAsync(w.Time, () => w.Service.GetDiscovered().Count == 2);

        await AdvanceUntilAsync(w.Time, () => !w.Service.IsSearching);

        w.AssertEndedAt(DiscoverySearchEnd.NothingFound, TimeSpan.FromSeconds(40));
        Assert.Empty(w.Service.GetDiscovered());
    }

    [Fact]
    public async Task Only_devices_ignored_before_the_search_end_it_at_40_seconds()
    {
        var mac = Observation("Mac Studio", "mac-studio.local", "192.168.1.20", new FakeTimeProvider(Start));
        var w = new World(new PreloadedIgnoredDeviceStore(mac.Identity.StableHash));
        await using var _ = w.Service;
        await w.Service.StartSearchAsync(_ => true);
        w.Browser.EmitFound(Observation("Mac Studio", "mac-studio.local", "192.168.1.20", w.Time));

        await AdvanceUntilAsync(w.Time, () => !w.Service.IsSearching);

        w.AssertEndedAt(DiscoverySearchEnd.NothingFound, TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task A_device_ignored_during_the_search_does_not_keep_it_alive()
    {
        var w = new World();
        await using var _ = w.Service;
        await w.Service.StartSearchAsync(_ => true);
        var mac = Observation("Mac Studio", "mac-studio.local", "192.168.1.20", w.Time);
        w.Browser.EmitFound(mac);
        await AdvanceUntilAsync(w.Time, () => w.Service.GetDiscovered().Count == 1);

        await w.Service.IgnoreAsync(mac.Identity);
        await AdvanceUntilAsync(w.Time, () => !w.Service.IsSearching);

        w.AssertEndedAt(DiscoverySearchEnd.NothingFound, TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task Duplicate_announcements_of_a_configured_server_end_the_search_at_40_seconds()
    {
        var w = new World();
        await using var _ = w.Service;
        await w.Service.StartSearchAsync(IsNotConfigured);

        // The same server on two interfaces, re-announced, and under a second instance name with the same address.
        for (var round = 0; round < 3; round++)
        {
            w.Browser.EmitFound(Observation("web-01", "web-01.local", "10.0.0.5", w.Time, nic: "nic-a"));
            w.Browser.EmitUpdated(Observation("web-01", "web-01.local", "10.0.0.5", w.Time, nic: "nic-b"));
            w.Browser.EmitFound(Observation("web-01-2", "web-01-2.local", "10.0.0.5", w.Time));
            await AdvanceUntilAsync(w.Time, () => w.Time.GetUtcNow() - Start >= TimeSpan.FromSeconds(10 * (round + 1)));
        }

        await AdvanceUntilAsync(w.Time, () => !w.Service.IsSearching);

        w.AssertEndedAt(DiscoverySearchEnd.NothingFound, TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task A_new_server_before_40_seconds_shows_at_once_and_keeps_the_search_to_the_10_minute_limit()
    {
        var w = new World(options: Options(DiscoveryOptions.Default.MaxSearchDuration));
        await using var _ = w.Service;
        var changes = 0;
        w.Service.DiscoveredChanged += (_, _) => Interlocked.Increment(ref changes);
        await w.Service.StartSearchAsync(IsNotConfigured);
        w.Browser.EmitFound(Observation("web-01", "web-01.local", "10.0.0.5", w.Time));
        await AdvanceUntilAsync(w.Time, () => w.Time.GetUtcNow() - Start >= TimeSpan.FromSeconds(30));

        var seen = Volatile.Read(ref changes);
        w.Browser.EmitFound(Observation("NAS", "nas.local", "192.168.1.30", w.Time));
        // At once: the next coalesced notification (100 ms), never a later sweep or the end of the search.
        await AdvanceUntilAsync(w.Time, () => Volatile.Read(ref changes) > seen, maxSteps: 4);
        Assert.Contains(w.Service.GetDiscovered(), service => service.DisplayName == "NAS");

        await AdvanceUntilAsync(w.Time, () => w.Time.GetUtcNow() - Start >= TimeSpan.FromSeconds(90), step: TimeSpan.FromSeconds(1));
        Assert.True(w.Service.IsSearching);

        await AdvanceUntilAsync(w.Time, () => !w.Service.IsSearching, step: TimeSpan.FromSeconds(1), maxSteps: 1200);
        w.AssertEndedAt(DiscoverySearchEnd.TimeLimit, TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), DiscoveryOptions.Default.MaxSearchDuration); // never raised
    }

    [Fact]
    public async Task A_usable_result_that_expired_before_40_seconds_still_counts()
    {
        var w = new World(options: Options(TimeSpan.FromMinutes(2), expiry: TimeSpan.FromSeconds(10)));
        await using var _ = w.Service;
        await w.Service.StartSearchAsync(IsNotConfigured);
        w.Browser.EmitFound(Observation("NAS", "nas.local", "192.168.1.30", w.Time));
        await AdvanceUntilAsync(w.Time, () => w.Service.GetDiscovered().Count == 1);

        await AdvanceUntilAsync(w.Time, () => w.Time.GetUtcNow() - Start >= TimeSpan.FromSeconds(60));

        Assert.Empty(w.Service.GetDiscovered());
        Assert.True(w.Service.IsSearching);
    }

    [Fact]
    public async Task A_suggestion_still_visible_at_40_seconds_is_judged_again()
    {
        // The starter could not tell yet (its configured servers were still being read) and later can: still visible at
        // 40 s, the new verdict decides. Unusable then usable keeps the search; the reverse is covered by the VM tests.
        var verdict = false;
        var w = new World(options: Options(TimeSpan.FromMinutes(2)));
        await using var _ = w.Service;
        await w.Service.StartSearchAsync(_ => Volatile.Read(ref verdict));
        w.Browser.EmitFound(Observation("NAS", "nas.local", "192.168.1.30", w.Time));
        await AdvanceUntilAsync(w.Time, () => w.Service.GetDiscovered().Count == 1);

        Volatile.Write(ref verdict, true);
        await AdvanceUntilAsync(w.Time, () => w.Time.GetUtcNow() - Start >= TimeSpan.FromSeconds(60));

        Assert.True(w.Service.IsSearching);
    }

    [Fact]
    public async Task A_usable_check_that_throws_counts_as_usable()
    {
        var w = new World(options: Options(TimeSpan.FromMinutes(2)));
        await using var _ = w.Service;
        await w.Service.StartSearchAsync(_ => throw new InvalidOperationException("synthetic"));
        w.Browser.EmitFound(Observation("NAS", "nas.local", "192.168.1.30", w.Time));

        await AdvanceUntilAsync(w.Time, () => w.Time.GetUtcNow() - Start >= TimeSpan.FromSeconds(60));

        Assert.True(w.Service.IsSearching);
        Assert.Single(w.Service.GetDiscovered());
    }

    [Fact]
    public async Task A_new_search_forgets_the_previous_searchs_usable_results()
    {
        var w = new World(options: Options(TimeSpan.FromMinutes(2)));
        await using var _ = w.Service;
        await w.Service.StartSearchAsync(_ => true);
        w.Browser.EmitFound(Observation("NAS", "nas.local", "192.168.1.30", w.Time));
        await AdvanceUntilAsync(w.Time, () => w.Service.GetDiscovered().Count == 1);
        await w.Service.StopSearchAsync();

        var restartedAt = w.Time.GetUtcNow();
        await w.Service.StartSearchAsync(_ => true);
        await AdvanceUntilAsync(w.Time, () => !w.Service.IsSearching);

        Assert.Equal(DiscoverySearchEnd.NothingFound, w.Service.LastSearchEnd);
        Assert.InRange(w.Time.GetUtcNow() - restartedAt, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(40) + Tolerance);
    }

    [Fact]
    public async Task A_stand_in_without_the_bound_still_starts_through_the_usable_overload()
    {
        IServerDiscoveryActivity activity = new StartOnlyActivity();

        await activity.StartSearchAsync(_ => false);

        Assert.True(activity.IsSearching);
    }

    // ------------------------------------------------------------------ helpers

    // Configured: web-01 (10.0.0.5, and its duplicates) and db-01 (10.0.0.6), by address like DashboardViewModel.IsAlreadyConfigured.
    private static bool IsNotConfigured(DiscoveredService service) =>
        !service.Addresses.Any(address => address.ToString() is "10.0.0.5" or "10.0.0.6");

    private static DiscoveryOptions Options(TimeSpan maxSearch, TimeSpan? expiry = null) => new()
    {
        ExpiryWindow = expiry ?? TimeSpan.FromDays(1),
        NothingFoundTimeout = TimeSpan.FromSeconds(40),
        MaxSearchDuration = maxSearch
    };

    private static DiscoveryObservation Observation(
        string instance, string host, string address, TimeProvider time, string nic = "nic-a") =>
        DiscoveryInputPolicy.TryCreateObservation(instance, "_ssh._tcp", "local.", host, 22,
            [IPAddress.Parse(address)], nic, time.GetUtcNow())!;

    internal static async Task AdvanceUntilAsync(
        FakeTimeProvider time, Func<bool> condition, TimeSpan? step = null, int maxSteps = 3000)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            time.Advance(step ?? TimeSpan.FromMilliseconds(250));
            await Task.Delay(2);
        }

        Assert.True(condition(), "condition not reached");
    }

    private sealed class World
    {
        private DateTimeOffset? _endedAt;

        public World(IIgnoredDeviceStore? ignored = null, DiscoveryOptions? options = null)
        {
            Service = new ServerDiscoveryService(
                Browser, ignored ?? new GatedIgnoredDeviceStore(), NullLogger<ServerDiscoveryService>.Instance, Time,
                options ?? Options(TimeSpan.FromMinutes(10)));
            Service.IsSearchingChanged += (_, _) =>
            {
                if (!Service.IsSearching)
                {
                    _endedAt ??= Time.GetUtcNow();
                }
            };
        }

        public FakeMdnsServiceBrowser Browser { get; } = new();

        public FakeTimeProvider Time { get; } = new(Start);

        public ServerDiscoveryService Service { get; }

        /// <summary>Ended as <paramref name="end"/> no earlier than <paramref name="bound"/>, and left no listener.</summary>
        public void AssertEndedAt(DiscoverySearchEnd end, TimeSpan bound)
        {
            Assert.Equal(end, Service.LastSearchEnd);
            Assert.NotNull(_endedAt);
            Assert.InRange(_endedAt.Value - Start, bound, bound + Tolerance);
            Assert.Equal(Browser.StartCount, Browser.StopCount);
            Assert.Equal(0, Browser.FoundSubscriberCount);
            Assert.Equal(0, Browser.UpdatedSubscriberCount);
            Assert.Equal(0, Browser.RemovedSubscriberCount);
        }
    }

    private sealed class PreloadedIgnoredDeviceStore(params string[] hashes) : IIgnoredDeviceStore
    {
        public Task<IReadOnlySet<string>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(hashes, StringComparer.Ordinal));

        public Task<bool> IgnoreAsync(string identityHash, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>A stand-in that implements only the original start (like the QA on-demand service).</summary>
    private sealed class StartOnlyActivity : IServerDiscoveryActivity
    {
        public bool IsSearching { get; private set; }

        public event EventHandler? IsSearchingChanged { add { } remove { } }

        public DiscoverySearchEnd LastSearchEnd => DiscoverySearchEnd.None;

        public Task StartSearchAsync(CancellationToken cancellationToken = default)
        {
            IsSearching = true;
            return Task.CompletedTask;
        }

        public Task StopSearchAsync(CancellationToken cancellationToken = default)
        {
            IsSearching = false;
            return Task.CompletedTask;
        }
    }
}
