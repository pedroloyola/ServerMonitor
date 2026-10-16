using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Discovery;
using ServerMonitor.Core.Interfaces;

namespace ServerMonitor.App.Tests.Services;

public sealed class ServerDiscoveryServiceTests
{
    private static readonly DateTimeOffset StartTime =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Found_AppearsWithExpectedSnapshot()
    {
        await using var h = await Harness.StartAsync();

        h.Browser.EmitFound(h.Observation("Mac Studio", "mac-studio", 22,
            ["192.168.1.42"], "ethernet"));

        var service = Assert.Single(h.Service.GetDiscovered());
        Assert.Equal("Mac Studio", service.DisplayName);
        Assert.Equal("mac-studio.local", service.HostName);
        Assert.Equal(22, service.Port);
        Assert.Equal([IPAddress.Parse("192.168.1.42")], service.Addresses);
        Assert.Equal(StartTime, service.FirstSeenAt);
        Assert.Equal(StartTime, service.LastSeenAt);
        await h.FlushNotificationsAsync(1);
        Assert.Equal(1, h.ChangeCount);
    }

    [Fact]
    public async Task DuplicateFoundAndRenewal_DeduplicateWithoutExtraEvent()
    {
        await using var h = await Harness.StartAsync();
        var first = h.Observation("Server", "server", addresses: ["10.0.0.4"]);
        h.Browser.EmitFound(first);

        h.Browser.EmitFound(first);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        h.Browser.EmitUpdated(h.Observation("SERVER", "SERVER.LOCAL.", addresses: ["10.0.0.4"]));

        var service = Assert.Single(h.Service.GetDiscovered());
        Assert.Equal(StartTime, service.FirstSeenAt);
        Assert.Equal(h.Time.GetUtcNow(), service.LastSeenAt);
        await h.FlushNotificationsAsync(1);
        Assert.Equal(1, h.ChangeCount);
    }

    [Fact]
    public async Task IPv4AndScopedIPv6AcrossTwoNics_MergeIntoOneService()
    {
        await using var h = await Harness.StartAsync();

        h.Browser.EmitFound(h.Observation("Server", "server", addresses: ["10.0.0.4"], interfaceId: "nic-v4"));
        h.Browser.EmitFound(h.Observation("server", "SERVER.LOCAL.", addresses: ["fe80::4%19"], interfaceId: "nic-v6"));

        var service = Assert.Single(h.Service.GetDiscovered());
        Assert.Equal(2, service.Addresses.Count);
        Assert.Contains(IPAddress.Parse("10.0.0.4"), service.Addresses);
        var v6 = Assert.Single(service.Addresses, address => address.AddressFamily ==
            System.Net.Sockets.AddressFamily.InterNetworkV6);
        Assert.Equal(19, v6.ScopeId);
    }

    [Fact]
    public async Task DuplicateAddressesAcrossNics_AreRetainedOnce()
    {
        await using var h = await Harness.StartAsync();
        h.Browser.EmitFound(h.Observation("Server", "server",
            addresses: ["10.0.0.4", "10.0.0.4"], interfaceId: "nic-a"));
        h.Browser.EmitFound(h.Observation("Server", "server",
            addresses: ["10.0.0.4", "10.0.0.5"], interfaceId: "nic-b"));

        var addresses = Assert.Single(h.Service.GetDiscovered()).Addresses;
        Assert.Equal([IPAddress.Parse("10.0.0.4"), IPAddress.Parse("10.0.0.5")], addresses);
    }

    [Fact]
    public async Task SameHostnameWithDistinctInstanceNames_RemainsDistinct()
    {
        await using var h = await Harness.StartAsync();
        h.Browser.EmitFound(h.Observation("Admin SSH", "server", 22, ["10.0.0.4"]));
        h.Browser.EmitFound(h.Observation("Maintenance SSH", "server", 22, ["10.0.0.4"]));

        var services = h.Service.GetDiscovered();
        Assert.Equal(2, services.Count);
        Assert.Equal(["Admin SSH", "Maintenance SSH"], services.Select(service => service.DisplayName));
    }

    [Fact]
    public async Task Updated_ChangesHostPortAndAddressWithoutChangingDiscoveryId()
    {
        await using var h = await Harness.StartAsync();
        h.Browser.EmitFound(h.Observation("Server", "old-host", 22, ["10.0.0.4"]));
        var discoveryId = Assert.Single(h.Service.GetDiscovered()).DiscoveryId;
        await h.FlushNotificationsAsync(1);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Browser.EmitUpdated(h.Observation("server", "new-host", 2222, ["10.0.0.9"]));

        var updated = Assert.Single(h.Service.GetDiscovered());
        Assert.Equal(discoveryId, updated.DiscoveryId);
        Assert.Equal("new-host.local", updated.HostName);
        Assert.Equal(2222, updated.Port);
        Assert.Equal([IPAddress.Parse("10.0.0.9")], updated.Addresses);
        await h.FlushNotificationsAsync(2);
        Assert.Equal(2, h.ChangeCount);
    }

    [Fact]
    public async Task OnlyMaterialChanges_RaiseChanged()
    {
        await using var h = await Harness.StartAsync();
        h.Browser.EmitFound(h.Observation("Server", "server", addresses: ["10.0.0.4"]));

        for (var index = 0; index < 1_000; index++)
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(1));
            h.Browser.EmitUpdated(h.Observation("Server", "server", addresses: ["10.0.0.4"]));
        }

        await h.FlushNotificationsAsync(1);
        Assert.Equal(1, h.ChangeCount);
        h.Browser.EmitUpdated(h.Observation("Server", "server", addresses: ["10.0.0.5"]));
        await h.FlushNotificationsAsync(2);
        Assert.Equal(2, h.ChangeCount);
    }

    // REL.0D O-8: Tmds.MDns 0.9.1 raises ServiceAdded once and then nothing while a host keeps answering the 30 s
    // re-queries with unchanged records (no TTL is stored, ServiceChanged needs a real difference). These tests model
    // that: Found once, no Updated, ever. The suggestion used to drop at 95 s although the host was still announcing.
    [Fact]
    public async Task FoundOnceWithoutAnyUpdate_StaysListedPast95SecondsAndForMinutes()
    {
        await using var h = await Harness.StartAsync();
        h.Browser.EmitFound(h.Observation("Server", "server"));
        var discoveryId = Assert.Single(h.Service.GetDiscovered()).DiscoveryId;

        foreach (var at in new[] { 94, 95, 96, 120, 300, 540 })
        {
            h.Time.Advance(TimeSpan.FromSeconds(at) - (h.Time.GetUtcNow() - StartTime));
            Assert.Equal(discoveryId, Assert.Single(h.Service.GetDiscovered()).DiscoveryId);
        }

        await h.FlushNotificationsAsync(1);
        Assert.Equal(1, h.ChangeCount); // the appearance only; nothing dropped and came back
    }

    [Fact]
    public async Task FoundOnceWithoutAnyUpdate_StaysListedForTheWholeDefaultSearch()
    {
        await using var h = await Harness.StartAsync();
        h.Browser.EmitFound(h.Observation("Server", "server"));

        for (var elapsed = TimeSpan.Zero; elapsed < DiscoveryOptions.Default.MaxSearchDuration; elapsed += TimeSpan.FromSeconds(1))
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(DiscoveryOptions.Default.MaxSearchDuration, h.Time.GetUtcNow() - StartTime);
        Assert.Single(h.Service.GetDiscovered());
    }

    [Fact]
    public void DefaultExpiry_NeverFiresInsideASearch()
    {
        // The search's own end clears the store; the expiry is only a backstop and must outlast the longest search,
        // with a margin for the sweep and the automatic stop.
        Assert.True(DiscoveryOptions.Default.ExpiryWindow >= DiscoveryOptions.Default.MaxSearchDuration + TimeSpan.FromMinutes(1),
            $"ExpiryWindow {DiscoveryOptions.Default.ExpiryWindow} must outlast MaxSearchDuration {DiscoveryOptions.Default.MaxSearchDuration} by 1 min");
        Assert.Equal(TimeSpan.FromMinutes(10), DiscoveryOptions.Default.MaxSearchDuration);
    }

    [Fact]
    public async Task GoodbyeLateInTheSearch_StillRemovesAfterGrace()
    {
        await using var h = await Harness.StartAsync();
        var observation = h.Observation("Server", "server");
        h.Browser.EmitFound(observation);
        h.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Single(h.Service.GetDiscovered());

        h.Browser.EmitRemoved(observation with { ObservedAt = h.Time.GetUtcNow() });
        h.Time.Advance(TimeSpan.FromMilliseconds(4_999));
        Assert.Single(h.Service.GetDiscovered());
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Empty(h.Service.GetDiscovered());
    }

    [Fact]
    public async Task Removed_RemainsDuringFiveSecondGraceThenDisappears()
    {
        await using var h = await Harness.StartAsync();
        var observation = h.Observation("Server", "server");
        h.Browser.EmitFound(observation);
        h.Browser.EmitRemoved(observation);

        h.Time.Advance(TimeSpan.FromMilliseconds(4_999));
        Assert.Single(h.Service.GetDiscovered());
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Empty(h.Service.GetDiscovered());
    }

    [Fact]
    public async Task RemovingOneNic_KeepsOtherNicVisibleAfterGrace()
    {
        await using var h = await Harness.StartAsync();
        var nicA = h.Observation("Server", "server", addresses: ["10.0.0.4"], interfaceId: "nic-a");
        var nicB = h.Observation("Server", "server", addresses: ["10.0.1.4"], interfaceId: "nic-b");
        h.Browser.EmitFound(nicA);
        h.Browser.EmitFound(nicB);

        h.Browser.EmitRemoved(nicA);
        h.Time.Advance(TimeSpan.FromSeconds(5));

        var visible = Assert.Single(h.Service.GetDiscovered());
        Assert.Equal([IPAddress.Parse("10.0.1.4")], visible.Addresses);
    }

    [Fact]
    public async Task RediscoveryDuringGrace_CancelsPendingRemoval()
    {
        await using var h = await Harness.StartAsync();
        var observation = h.Observation("Server", "server");
        h.Browser.EmitFound(observation);
        var discoveryId = Assert.Single(h.Service.GetDiscovered()).DiscoveryId;
        h.Browser.EmitRemoved(observation);

        h.Time.Advance(TimeSpan.FromSeconds(4));
        h.Browser.EmitUpdated(h.Observation("Server", "server"));
        h.Time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(discoveryId, Assert.Single(h.Service.GetDiscovered()).DiscoveryId);
        await h.FlushNotificationsAsync(1);
        Assert.Equal(1, h.ChangeCount);
    }

    [Fact]
    public async Task IgnoreA_HidesOnlyA_AndResetRevealsStillPresentService()
    {
        await using var h = await Harness.StartAsync();
        h.Browser.EmitFound(h.Observation("A", "a"));
        h.Browser.EmitFound(h.Observation("B", "b"));
        var a = h.Service.GetDiscovered().Single(service => service.DisplayName == "A");

        await h.Service.IgnoreAsync(a.Identity);

        Assert.Equal("B", Assert.Single(h.Service.GetDiscovered()).DisplayName);
        Assert.Contains(a.Identity.StableHash, h.Store.Entries);

        await h.Service.ResetIgnoredAsync();
        Assert.Equal(["A", "B"], h.Service.GetDiscovered().Select(service => service.DisplayName));
    }

    [Fact]
    public async Task IgnoreRefusal_LeavesSuggestionVisible()
    {
        var store = new FakeIgnoredDeviceStore { AcceptIgnore = false };
        await using var h = await Harness.StartAsync(store);
        h.Browser.EmitFound(h.Observation("A", "a"));
        var a = Assert.Single(h.Service.GetDiscovered());

        await h.Service.IgnoreAsync(a.Identity);

        Assert.Single(h.Service.GetDiscovered());
        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task PreloadedIgnore_IsAppliedBeforeBrowserStarts()
    {
        var identity = ServiceInstanceIdentity.TryCreate("A", "_ssh._tcp", "local")!;
        var store = new FakeIgnoredDeviceStore([identity.StableHash]);
        await using var h = await Harness.StartAsync(store);

        h.Browser.EmitFound(h.Observation("A", "a"));

        Assert.Empty(h.Service.GetDiscovered());
        Assert.Equal(1, store.LoadCount);
    }

    [Fact]
    public async Task StartAndStop_AreIdempotentAndManageOneSubscription()
    {
        var h = await Harness.StartAsync();
        await h.Service.StartAsync();

        Assert.Equal(1, h.Browser.StartCount);
        Assert.Equal(1, h.Browser.FoundSubscriberCount);
        Assert.Equal(1, h.Browser.UpdatedSubscriberCount);
        Assert.Equal(1, h.Browser.RemovedSubscriberCount);

        await h.Service.StopAsync();
        await h.Service.StopAsync();

        Assert.Equal(1, h.Browser.StopCount);
        Assert.Equal(0, h.Browser.FoundSubscriberCount);
        Assert.Equal(0, h.Browser.UpdatedSubscriberCount);
        Assert.Equal(0, h.Browser.RemovedSubscriberCount);
        await h.DisposeAsync();
    }

    [Fact]
    public async Task CancelledStart_RollsBackAndCanRetry()
    {
        var store = new FakeIgnoredDeviceStore { BlockLoad = true };
        var h = Harness.Create(store);
        using var cancellation = new CancellationTokenSource();
        var start = h.Service.StartAsync(cancellation.Token);
        await store.LoadEntered.Task;

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);

        Assert.Equal(0, h.Browser.FoundSubscriberCount);
        Assert.Equal(1, h.Browser.StopCount);
        store.ReleaseLoad();
        await h.Service.StartAsync();
        Assert.Equal(1, h.Browser.StartCount);
        Assert.Equal(1, h.Browser.FoundSubscriberCount);
        await h.DisposeAsync();
    }

    [Fact]
    public async Task FailedBrowserStart_RollsBackAndRetryHasNoDuplicateHandlers()
    {
        var h = Harness.Create();
        h.Browser.StartException = new InvalidOperationException("synthetic");

        await h.Service.StartAsync();
        Assert.Equal(0, h.Browser.FoundSubscriberCount);
        Assert.Equal(1, h.Browser.StopCount);

        h.Browser.StartException = null;
        await h.Service.StartAsync();
        Assert.Equal(2, h.Browser.StartCount);
        Assert.Equal(1, h.Browser.FoundSubscriberCount);
        await h.DisposeAsync();
    }

    // M14.5: the dashboard's "looking on your local network" indicator reads this. It must be true only
    // while the browser is really started — never before a start, after a stop, or when a start failed.
    [Fact]
    public async Task IsSearching_IsTrueOnlyBetweenASuccessfulStartAndStop()
    {
        var h = Harness.Create();
        IServerDiscoveryActivity activity = h.Service;
        var observed = new List<bool>();
        activity.IsSearchingChanged += (_, _) => observed.Add(activity.IsSearching);

        Assert.False(activity.IsSearching);

        await h.Service.StartAsync();
        Assert.True(activity.IsSearching);
        await h.Service.StartAsync();

        await h.Service.StopAsync();
        Assert.False(activity.IsSearching);
        await h.Service.StopAsync();

        await h.Service.StartAsync();
        Assert.True(activity.IsSearching);
        await h.DisposeAsync();
        Assert.False(activity.IsSearching);

        // One notification per real change; idempotent starts/stops raise nothing.
        Assert.Equal([true, false, true, false], observed);
    }

    [Fact]
    public async Task IsSearching_StaysFalseWhenTheBrowserFailsToStart()
    {
        var h = Harness.Create();
        IServerDiscoveryActivity activity = h.Service;
        var notifications = 0;
        activity.IsSearchingChanged += (_, _) => notifications++;
        h.Browser.StartException = new InvalidOperationException("synthetic");

        await h.Service.StartAsync();

        Assert.False(activity.IsSearching);
        // REL.0B (Cortex N-3): the failure itself is signalled, once, so the dashboard can say so.
        Assert.Equal(DiscoverySearchEnd.Failed, activity.LastSearchEnd);
        Assert.Equal(1, notifications);

        h.Browser.StartException = null;
        await h.Service.StartAsync();
        Assert.True(activity.IsSearching);
        Assert.Equal(DiscoverySearchEnd.None, activity.LastSearchEnd);
        Assert.Equal(2, notifications);
        await h.DisposeAsync();
    }

    [Fact]
    public async Task IsSearching_StaysFalseWhenTheStartIsCancelled()
    {
        var store = new FakeIgnoredDeviceStore { BlockLoad = true };
        var h = Harness.Create(store);
        IServerDiscoveryActivity activity = h.Service;
        using var cancellation = new CancellationTokenSource();
        var start = h.Service.StartAsync(cancellation.Token);
        await store.LoadEntered.Task;

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);

        Assert.False(activity.IsSearching);
        store.ReleaseLoad();
        await h.DisposeAsync();
    }

    [Fact]
    public async Task ThrowingActivitySubscriber_DoesNotBreakStartOrStop()
    {
        var h = Harness.Create();
        IServerDiscoveryActivity activity = h.Service;
        activity.IsSearchingChanged += (_, _) => throw new InvalidOperationException("synthetic subscriber");

        await h.Service.StartAsync();
        Assert.True(activity.IsSearching);
        Assert.Equal(1, h.Browser.FoundSubscriberCount);

        await h.Service.StopAsync();
        Assert.False(activity.IsSearching);
        Assert.Equal(1, h.Browser.StopCount);
        await h.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentStartAndStop_AreSerializedWithoutOrphanSubscription()
    {
        var store = new FakeIgnoredDeviceStore { BlockLoad = true };
        var h = Harness.Create(store);
        var start = h.Service.StartAsync();
        await store.LoadEntered.Task;
        var stop = h.Service.StopAsync();

        store.ReleaseLoad();
        await start;
        await stop;

        Assert.Equal(1, h.Browser.StartCount);
        Assert.Equal(1, h.Browser.StopCount);
        Assert.Equal(0, h.Browser.FoundSubscriberCount);
        h.Browser.EmitFound(h.Observation("orphan", "orphan"));
        Assert.Empty(h.Service.GetDiscovered());
        await h.DisposeAsync();
    }

    [Fact]
    public async Task RestartAndInterfaceChurn_DoNotDuplicateHandlersOrSuggestions()
    {
        var h = await Harness.StartAsync();
        await h.Service.StopAsync();
        await h.Service.StartAsync();
        Assert.Equal(1, h.Browser.FoundSubscriberCount);

        var nic = h.Observation("Server", "server", interfaceId: "nic-a");
        h.Browser.EmitFound(nic);
        h.Browser.EmitRemoved(nic);
        h.Browser.EmitUpdated(h.Observation("Server", "server", interfaceId: "nic-a"));

        Assert.Single(h.Service.GetDiscovered());
        Assert.Equal(2, h.Browser.StartCount);
        await h.FlushNotificationsAsync(1);
        Assert.Equal(1, h.ChangeCount);
        await h.DisposeAsync();
    }

    [Fact]
    public async Task UniqueFlood_IsCappedAndCoalescedIntoOneNotification()
    {
        await using var h = await Harness.StartAsync();
        for (var index = 0; index < DiscoveryInputPolicy.MaxVisibleServices + 100; index++)
        {
            h.Browser.EmitFound(h.Observation($"Server {index:D4}", $"server-{index:D4}"));
        }

        Assert.Equal(DiscoveryInputPolicy.MaxVisibleServices, h.Service.GetDiscovered().Count);
        Assert.Equal(0, h.ChangeCount);
        await h.FlushNotificationsAsync(1);
        Assert.Equal(1, h.ChangeCount);
        var afterUnique = h.ChangeCount;
        var existing = h.Observation("Server 0000", "server-0000");
        for (var index = 0; index < 10_000; index++)
        {
            h.Browser.EmitUpdated(existing);
        }

        Assert.Equal(afterUnique, h.ChangeCount);
        Assert.Equal(DiscoveryInputPolicy.MaxVisibleServices, h.Service.GetDiscovered().Count);
    }

    [Fact]
    public async Task MaterialChangeDuringNotification_SchedulesExactlyOneFollowUpNotification()
    {
        await using var h = await Harness.StartAsync();
        var injectedSecondService = false;
        h.OnDiscoveredChanged((_, _) =>
        {
            if (injectedSecondService)
            {
                return;
            }

            injectedSecondService = true;
            Assert.Equal("First", Assert.Single(h.Service.GetDiscovered()).DisplayName);
            h.Browser.EmitFound(h.Observation("Second", "second"));
        });

        h.Browser.EmitFound(h.Observation("First", "first"));
        await h.FlushNotificationsAsync(1);

        Assert.Equal(1, h.ChangeCount);
        Assert.Equal(2, h.Service.GetDiscovered().Count);

        await h.FlushNotificationsAsync(2);

        Assert.Equal(2, h.ChangeCount);
        Assert.Equal(["First", "Second"],
            h.Service.GetDiscovered().Select(service => service.DisplayName));
    }

    [Fact]
    public async Task CapturedCallbackBeforeStop_CannotRepopulateAfterStopOrRestart()
    {
        var h = await Harness.StartAsync();
        var staleHandler = h.Browser.CaptureFoundHandler();
        Assert.NotNull(staleHandler);

        await h.Service.StopAsync();
        h.Browser.EmitCaptured(staleHandler, h.Observation("Stale", "stale"));
        Assert.Empty(h.Service.GetDiscovered());

        await h.Service.StartAsync();
        h.Browser.EmitCaptured(staleHandler, h.Observation("Still stale", "still-stale"));
        Assert.Empty(h.Service.GetDiscovered());

        h.Browser.EmitFound(h.Observation("Current", "current"));
        Assert.Equal("Current", Assert.Single(h.Service.GetDiscovered()).DisplayName);
        await h.FlushNotificationsAsync(1);
        Assert.Equal(1, h.ChangeCount);
        await h.DisposeAsync();
    }

    [Fact]
    public async Task PendingNotification_IsCancelledAndDrainedByStop()
    {
        var h = await Harness.StartAsync();
        h.Browser.EmitFound(h.Observation("Server", "server"));
        Assert.Equal(0, h.ChangeCount);

        await h.Service.StopAsync();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await Task.Yield();

        Assert.Equal(0, h.ChangeCount);
        Assert.Empty(h.Service.GetDiscovered());
        await h.DisposeAsync();
    }

    [Fact]
    public async Task IgnoredFlood_DoesNotConsumeVisibleSuggestionReserve()
    {
        var ignoredIdentities = Enumerable.Range(0, DiscoveryInputPolicy.MaxVisibleServices)
            .Select(index => ServiceInstanceIdentity.TryCreate(
                $"Ignored {index:D4}", "_ssh._tcp", "local")!)
            .ToList();
        var store = new FakeIgnoredDeviceStore(ignoredIdentities.Select(identity => identity.StableHash));
        await using var h = await Harness.StartAsync(store);

        for (var index = 0; index < ignoredIdentities.Count; index++)
        {
            h.Browser.EmitFound(h.Observation($"Ignored {index:D4}", $"ignored-{index:D4}"));
        }

        h.Browser.EmitFound(h.Observation("Legitimate", "legitimate"));

        Assert.Equal("Legitimate", Assert.Single(h.Service.GetDiscovered()).DisplayName);
        await h.Service.ResetIgnoredAsync();
        Assert.Equal(DiscoveryInputPolicy.MaxVisibleServices, h.Service.GetDiscovered().Count);
        Assert.Contains(h.Service.GetDiscovered(), item => item.DisplayName == "Ignored 0000");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(FakeIgnoredDeviceStore store)
        {
            Store = store;
            Browser = new FakeMdnsServiceBrowser();
            Time = new FakeTimeProvider(StartTime);
            Service = new ServerDiscoveryService(Browser, Store,
                NullLogger<ServerDiscoveryService>.Instance, Time, new DiscoveryOptions
                {
                    // REL.0D O-8: the production expiry, so no test here leans on a renewal the real browser never sends.
                    ExpiryWindow = DiscoveryOptions.Default.ExpiryWindow,
                    RemovalGrace = TimeSpan.FromSeconds(5),
                    SweepInterval = TimeSpan.FromSeconds(1),
                    ChangeNotificationDelay = TimeSpan.FromMilliseconds(100),
                    StopDrainTimeout = TimeSpan.FromSeconds(5),
                    // REL.0B search bounds are covered by DiscoverySearchBoundsTests; out of the way here.
                    NothingFoundTimeout = TimeSpan.FromDays(1),
                    MaxSearchDuration = TimeSpan.FromDays(2)
                });
            // The harness's ONLY subscription, made before the service can raise anything. Subscribing per flush
            // raced a raise already in flight: the service invokes the delegate it read before that subscription.
            Service.DiscoveredChanged += CountChange;
        }

        /// <summary>The notification delay configured on the service under test.</summary>
        private static readonly TimeSpan NotificationDelay = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// A deadline for producing a readable failure, never the thing being measured. If a flush
        /// ever gets near it, the notification did not arrive at all and the test should say so
        /// rather than hang the run.
        /// </summary>
        private static readonly TimeSpan FlushDeadline = TimeSpan.FromSeconds(30);

        private readonly object _notificationGate = new();
        private readonly List<EventHandler> _testHandlers = [];
        private readonly List<(int Expected, TaskCompletionSource Delivered)> _waiters = [];
        private int _changeCount;

        public FakeMdnsServiceBrowser Browser { get; }
        public FakeIgnoredDeviceStore Store { get; }
        public FakeTimeProvider Time { get; }
        public ServerDiscoveryService Service { get; }

        public int ChangeCount
        {
            get { lock (_notificationGate) { return _changeCount; } }
        }

        /// <summary>
        /// A test's own reaction to <c>DiscoveredChanged</c>. It runs inside the harness's subscription, BEFORE the
        /// change is counted, so a flush never returns while it is still running (one test discovers a second service
        /// from inside it).
        /// </summary>
        public void OnDiscoveredChanged(EventHandler handler)
        {
            lock (_notificationGate)
            {
                _testHandlers.Add(handler);
            }
        }

        private void CountChange(object? sender, EventArgs args)
        {
            EventHandler[] handlers;
            lock (_notificationGate)
            {
                handlers = [.. _testHandlers];
            }

            try
            {
                foreach (var handler in handlers)
                {
                    handler(sender, args);
                }
            }
            finally
            {
                // Counted and signalled under one lock, after the test's handlers: a waiter registered before this
                // point is completed here, one registered after it sees the count.
                lock (_notificationGate)
                {
                    _changeCount++;
                    foreach (var waiter in _waiters.Where(w => _changeCount >= w.Expected).ToList())
                    {
                        waiter.Delivered.TrySetResult();
                        _waiters.Remove(waiter);
                    }
                }
            }
        }

        /// <summary>
        /// Advances the virtual clock past the coalescing window and waits until the service has
        /// actually raised <c>DiscoveredChanged</c> the expected number of times.
        /// </summary>
        /// <remarks>
        /// This used to advance the clock and then <c>await Task.Yield()</c> ten times. Advancing the
        /// clock only completes the service's <c>Task.Delay</c>; the continuation that raises the
        /// event is then queued on the thread pool, and ten yields are a guess about scheduling, not
        /// a guarantee that it ran. On a machine where the pool is contended the assertion could run
        /// first and read a stale count, which is a test that fails for reasons the product does not
        /// control.
        ///
        /// The waiter is registered BEFORE the clock is advanced, under the lock the count moves under,
        /// so a notification that lands immediately (or is already in flight) cannot be missed, and an
        /// expectation that is already satisfied returns without waiting at all — some tests advance time
        /// enough for the notification to have fired before they flush.
        ///
        /// REL.0E: the waiter used to be a handler subscribed here. A raise already in flight had read the
        /// delegate before that subscription, so it never called it, yet its count could move after the
        /// pre-check: "raised 1 time(s); waiting for 1", then the deadline. Counting and signalling now
        /// happen in the one handler subscribed in the constructor (see <see cref="CountChange"/>).
        /// </remarks>
        public async Task FlushNotificationsAsync(int expectedChangeCount)
        {
            TaskCompletionSource? delivered = null;
            lock (_notificationGate)
            {
                if (_changeCount < expectedChangeCount)
                {
                    delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.Add((expectedChangeCount, delivered));
                }
            }

            Time.Advance(NotificationDelay);

            if (delivered is null)
            {
                return;
            }

            try
            {
                await delivered.Task.WaitAsync(FlushDeadline);
            }
            catch (TimeoutException)
            {
                lock (_notificationGate)
                {
                    _waiters.RemoveAll(waiter => ReferenceEquals(waiter.Delivered, delivered));
                }

                throw new InvalidOperationException(
                    $"DiscoveredChanged was raised {ChangeCount} time(s); the flush was waiting for "
                        + $"{expectedChangeCount}. The notification never arrived, so this is not slowness.");
            }
        }

        public static Harness Create(FakeIgnoredDeviceStore? store = null) => new(store ?? new());

        public static async Task<Harness> StartAsync(FakeIgnoredDeviceStore? store = null)
        {
            var harness = Create(store);
            await harness.Service.StartAsync();
            return harness;
        }

        public DiscoveryObservation Observation(
            string instance,
            string host,
            int port = 22,
            IReadOnlyList<string>? addresses = null,
            string interfaceId = "nic-a") =>
            DiscoveryInputPolicy.TryCreateObservation(instance, "_ssh._tcp", "local.", host, port,
                (addresses ?? ["192.168.1.20"]).Select(IPAddress.Parse), interfaceId, Time.GetUtcNow())!;

        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    private sealed class FakeIgnoredDeviceStore : IIgnoredDeviceStore
    {
        private readonly HashSet<string> _entries;
        private readonly TaskCompletionSource<bool> _releaseLoad =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeIgnoredDeviceStore(IEnumerable<string>? entries = null)
        {
            _entries = new HashSet<string>(entries ?? [], StringComparer.Ordinal);
        }

        public bool AcceptIgnore { get; init; } = true;
        public bool BlockLoad { get; init; }
        public int LoadCount { get; private set; }
        public IReadOnlySet<string> Entries => _entries;
        public TaskCompletionSource<bool> LoadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlySet<string>> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            LoadEntered.TrySetResult(true);
            if (BlockLoad && !_releaseLoad.Task.IsCompleted)
            {
                await _releaseLoad.Task.WaitAsync(cancellationToken);
            }

            return new HashSet<string>(_entries, StringComparer.Ordinal);
        }

        public Task<bool> IgnoreAsync(string identityHash, CancellationToken cancellationToken = default)
        {
            if (!AcceptIgnore)
            {
                return Task.FromResult(false);
            }

            _entries.Add(identityHash);
            return Task.FromResult(true);
        }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            _entries.Clear();
            return Task.CompletedTask;
        }

        public void ReleaseLoad() => _releaseLoad.TrySetResult(true);
    }
}
