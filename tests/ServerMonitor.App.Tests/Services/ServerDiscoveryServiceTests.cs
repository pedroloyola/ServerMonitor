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

    [Fact]
    public async Task RenewalRestartsNinetyFiveSecondExpiryWindow()
    {
        await using var h = await Harness.StartAsync();
        h.Browser.EmitFound(h.Observation("Server", "server"));

        h.Time.Advance(TimeSpan.FromSeconds(90));
        h.Browser.EmitUpdated(h.Observation("Server", "server"));
        h.Time.Advance(TimeSpan.FromSeconds(94));
        Assert.Single(h.Service.GetDiscovered());
    }

    [Fact]
    public async Task ObservationExpiresAtNinetyFiveSecondsWithoutRealWaiting()
    {
        await using var h = await Harness.StartAsync();
        h.Browser.EmitFound(h.Observation("Server", "server"));

        h.Time.Advance(TimeSpan.FromSeconds(94));
        Assert.Single(h.Service.GetDiscovered());
        h.Time.Advance(TimeSpan.FromSeconds(1));
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
        h.Service.DiscoveredChanged += (_, _) =>
        {
            if (injectedSecondService)
            {
                return;
            }

            injectedSecondService = true;
            Assert.Equal("First", Assert.Single(h.Service.GetDiscovered()).DisplayName);
            h.Browser.EmitFound(h.Observation("Second", "second"));
        };

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
                    ExpiryWindow = TimeSpan.FromSeconds(95),
                    RemovalGrace = TimeSpan.FromSeconds(5),
                    SweepInterval = TimeSpan.FromSeconds(1),
                    ChangeNotificationDelay = TimeSpan.FromMilliseconds(100),
                    StopDrainTimeout = TimeSpan.FromSeconds(5)
                });
            Service.DiscoveredChanged += (_, _) =>
            {
                lock (_notificationGate)
                {
                    _changeCount++;
                }
            };
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
        /// The count is registered BEFORE the clock is advanced, so a notification that lands
        /// immediately cannot be missed, and an expectation that is already satisfied returns without
        /// waiting at all — some tests advance time enough for the notification to have fired before
        /// they flush.
        /// </remarks>
        public async Task FlushNotificationsAsync(int expectedChangeCount)
        {
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void Signal(object? sender, EventArgs args)
            {
                lock (_notificationGate)
                {
                    if (_changeCount < expectedChangeCount)
                    {
                        return;
                    }
                }

                delivered.TrySetResult();
            }

            // Subscribed HERE and not in the constructor, so it is the last handler in the multicast
            // and runs after any handler the test itself added. A subscriber that reacts to the
            // notification — one of these tests discovers a second service from inside it — must have
            // finished before the flush returns, or the test resumes against a half-applied world and
            // sees one service where two exist.
            Service.DiscoveredChanged += Signal;
            try
            {
                lock (_notificationGate)
                {
                    if (_changeCount >= expectedChangeCount)
                    {
                        delivered.TrySetResult();
                    }
                }

                Time.Advance(NotificationDelay);

                try
                {
                    await delivered.Task.WaitAsync(FlushDeadline);
                }
                catch (TimeoutException)
                {
                    throw new InvalidOperationException(
                        $"DiscoveredChanged was raised {ChangeCount} time(s); the flush was waiting for "
                            + $"{expectedChangeCount}. The notification never arrived, so this is not slowness.");
                }
            }
            finally
            {
                Service.DiscoveredChanged -= Signal;
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
