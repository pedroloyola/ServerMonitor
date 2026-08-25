using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Alerts;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.Services;

public sealed class ServerAlertCoordinatorTests
{
    [Theory]
    [InlineData(ServerHealth.Healthy)]
    [InlineData(ServerHealth.Warning)]
    [InlineData(ServerHealth.Critical)]
    [InlineData(ServerHealth.Offline)]
    [InlineData(ServerHealth.Unknown)]
    public async Task InitialObservation_EstablishesBaselineWithoutNotification(ServerHealth health)
    {
        await using var harness = await Harness.CreateAsync();

        harness.SetHealth(health);

        Assert.Empty(harness.Notifications.Items);
    }

    [Theory]
    [InlineData(ServerHealth.Healthy, ServerHealth.Warning, ServerAlertCategory.Warning)]
    [InlineData(ServerHealth.Healthy, ServerHealth.Critical, ServerAlertCategory.Critical)]
    [InlineData(ServerHealth.Warning, ServerHealth.Critical, ServerAlertCategory.Critical)]
    [InlineData(ServerHealth.Healthy, ServerHealth.Offline, ServerAlertCategory.Offline)]
    [InlineData(ServerHealth.Warning, ServerHealth.Offline, ServerAlertCategory.Offline)]
    [InlineData(ServerHealth.Critical, ServerHealth.Offline, ServerAlertCategory.Offline)]
    [InlineData(ServerHealth.Offline, ServerHealth.Healthy, ServerAlertCategory.Recovery)]
    [InlineData(ServerHealth.Offline, ServerHealth.Warning, ServerAlertCategory.Recovery)]
    [InlineData(ServerHealth.Offline, ServerHealth.Critical, ServerAlertCategory.Recovery)]
    [InlineData(ServerHealth.Warning, ServerHealth.Healthy, ServerAlertCategory.Recovery)]
    [InlineData(ServerHealth.Critical, ServerHealth.Healthy, ServerAlertCategory.Recovery)]
    public async Task Transition_SendsExactlyExpectedNotification(
        ServerHealth initial,
        ServerHealth current,
        ServerAlertCategory expected)
    {
        await using var harness = await Harness.CreateAsync(initial);

        harness.SetHealth(current);
        await harness.Notifications.WaitForCountAsync(1);

        var notification = Assert.Single(harness.Notifications.Items);
        Assert.Equal(expected, notification.Category);
        Assert.Equal(harness.Server.Id, notification.ServerId);
        Assert.DoesNotContain(harness.Server.Host, notification.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CriticalToWarning_IsSilent()
    {
        await using var harness = await Harness.CreateAsync(ServerHealth.Critical);

        harness.SetHealth(ServerHealth.Warning);

        Assert.Empty(harness.Notifications.Items);
    }

    [Theory]
    [InlineData(ServerHealth.Warning)]
    [InlineData(ServerHealth.Critical)]
    [InlineData(ServerHealth.Offline)]
    public async Task RepeatedHealthAcrossTwentyCycles_IsDeduplicated(ServerHealth health)
    {
        await using var harness = await Harness.CreateAsync(ServerHealth.Healthy);
        harness.SetHealth(health);
        await harness.Notifications.WaitForCountAsync(1);

        for (var cycle = 0; cycle < 20; cycle++)
        {
            harness.SetHealth(health, isRefreshing: cycle % 2 == 0);
        }

        Assert.Single(harness.Notifications.Items);
    }

    [Fact]
    public async Task SameCategoryInsideCooldown_IsSuppressed_AndExactBoundaryIsAllowed()
    {
        await using var harness = await Harness.CreateAsync(ServerHealth.Healthy);
        harness.SetHealth(ServerHealth.Warning);
        await harness.Notifications.WaitForCountAsync(1);
        harness.SetHealth(ServerHealth.Healthy);
        await harness.Notifications.WaitForCountAsync(2);

        harness.SetHealth(ServerHealth.Warning);
        Assert.Single(harness.Notifications.Items, item => item.Category == ServerAlertCategory.Warning);

        harness.Time.Advance(ServerAlertCoordinator.DefaultCooldown);
        harness.SetHealth(ServerHealth.Healthy);
        await harness.Notifications.WaitForCountAsync(3);
        harness.SetHealth(ServerHealth.Warning);
        await harness.Notifications.WaitForCountAsync(4);

        Assert.Equal(2, harness.Notifications.Items.Count(item => item.Category == ServerAlertCategory.Warning));
    }

    [Fact]
    public async Task HigherSeverityCategories_BypassOtherCategoryCooldowns()
    {
        await using var harness = await Harness.CreateAsync(ServerHealth.Healthy);

        harness.SetHealth(ServerHealth.Warning);
        await harness.Notifications.WaitForCountAsync(1);
        harness.SetHealth(ServerHealth.Critical);
        await harness.Notifications.WaitForCountAsync(2);
        harness.SetHealth(ServerHealth.Offline);
        await harness.Notifications.WaitForCountAsync(3);

        Assert.Equal(
            [ServerAlertCategory.Warning, ServerAlertCategory.Critical, ServerAlertCategory.Offline],
            harness.Notifications.Items.Select(item => item.Category));
    }

    [Fact]
    public async Task CriticalEscalation_BypassesEarlierCriticalCategoryCooldown()
    {
        await using var harness = await Harness.CreateAsync(ServerHealth.Healthy);
        harness.SetHealth(ServerHealth.Critical);
        await harness.Notifications.WaitForCountAsync(1);
        harness.SetHealth(ServerHealth.Warning);
        harness.SetHealth(ServerHealth.Critical);
        await harness.Notifications.WaitForCountAsync(2);

        Assert.Equal(
            2,
            harness.Notifications.Items.Count(item => item.Category == ServerAlertCategory.Critical));
    }

    [Fact]
    public async Task NewOfflineOutage_BypassesEarlierOfflineCategoryCooldown()
    {
        await using var harness = await Harness.CreateAsync(ServerHealth.Healthy);
        harness.SetHealth(ServerHealth.Offline);
        await harness.Notifications.WaitForCountAsync(1);
        harness.SetHealth(ServerHealth.Healthy);
        await harness.Notifications.WaitForCountAsync(2);
        harness.SetHealth(ServerHealth.Offline);
        await harness.Notifications.WaitForCountAsync(3);

        Assert.Equal(
            2,
            harness.Notifications.Items.Count(item => item.Category == ServerAlertCategory.Offline));
    }

    [Fact]
    public async Task Disabled_TracksBaselineWithoutCalls_AndReenableDoesNotReplay()
    {
        await using var harness = await Harness.CreateAsync(ServerHealth.Healthy);
        harness.Settings.SetNotificationsEnabled(false);

        harness.SetHealth(ServerHealth.Warning);
        harness.Settings.SetNotificationsEnabled(true);
        Assert.Empty(harness.Notifications.Items);

        harness.SetHealth(ServerHealth.Critical);
        await harness.Notifications.WaitForCountAsync(1);

        Assert.Equal(ServerAlertCategory.Critical, Assert.Single(harness.Notifications.Items).Category);
    }

    [Fact]
    public async Task DisableAndReenable_FencesAnIntentAlreadyBeingResolved()
    {
        await using var harness = await Harness.CreateAsync(ServerHealth.Healthy);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Servers.GetAllOverride = async cancellationToken =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return harness.Servers.Servers.ToList();
        };

        harness.SetHealth(ServerHealth.Warning);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.Settings.SetNotificationsEnabled(false);
        harness.Settings.SetNotificationsEnabled(true);
        release.TrySetResult();
        await harness.Coordinator.FlushAsync();

        Assert.Empty(harness.Notifications.Items);

        harness.Servers.GetAllOverride = null;
        harness.SetHealth(ServerHealth.Healthy);
        await harness.Notifications.WaitForCountAsync(1);
        harness.SetHealth(ServerHealth.Warning);
        await harness.Notifications.WaitForCountAsync(2);

        Assert.Contains(
            harness.Notifications.Items,
            item => item.Category == ServerAlertCategory.Warning);
    }

    [Fact]
    public async Task RemovedServer_ClearsCooldownBeforeSameIdIsObservedAgain()
    {
        await using var harness = await Harness.CreateAsync(ServerHealth.Healthy);
        harness.SetHealth(ServerHealth.Warning);
        await harness.Notifications.WaitForCountAsync(1);

        harness.States.Remove(harness.Server.Id);
        harness.States.Set(ServerMonitoringState.Initial(harness.Server.Id) with
        {
            Health = ServerHealth.Healthy
        });
        harness.SetHealth(ServerHealth.Warning);
        await harness.Notifications.WaitForCountAsync(2);

        Assert.Equal(
            2,
            harness.Notifications.Items.Count(item => item.Category == ServerAlertCategory.Warning));
    }

    [Fact]
    public async Task HiddenConfiguredServer_StillNotifies()
    {
        var server = TestData.LinuxServer() with { IsHidden = true };
        await using var harness = await Harness.CreateAsync(ServerHealth.Healthy, server);

        harness.SetHealth(ServerHealth.Offline);
        await harness.Notifications.WaitForCountAsync(1);

        Assert.Equal(ServerAlertCategory.Offline, Assert.Single(harness.Notifications.Items).Category);
    }

    [Fact]
    public async Task StateForNonConfiguredServer_IsNotDelivered()
    {
        await using var harness = await Harness.CreateAsync(ServerHealth.Healthy);
        harness.Servers.Servers.Clear();

        harness.SetHealth(ServerHealth.Offline);
        await harness.Coordinator.FlushAsync();

        Assert.Empty(harness.Notifications.Items);
    }

    [Theory]
    [InlineData("normal server", "normal server")]
    [InlineData("M\u00e1quina \ud83d\ude80", "M\u00e1quina \ud83d\ude80")]
    [InlineData("line\r\nname\tend", "line name end")]
    [InlineData("safe\u202Etxt.exe", "safetxt.exe")]
    [InlineData("safe\u206Atxt.exe", "safetxt.exe")]
    public async Task UntrustedServerName_IsSanitizedForPresentation(string name, string expected)
    {
        var server = TestData.LinuxServer() with { Name = name };
        await using var harness = await Harness.CreateAsync(ServerHealth.Healthy, server);

        harness.SetHealth(ServerHealth.Offline);
        await harness.Notifications.WaitForCountAsync(1);

        Assert.Contains(expected, Assert.Single(harness.Notifications.Items).Body, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', harness.Notifications.Items.Single().Body);
        Assert.DoesNotContain('\n', harness.Notifications.Items.Single().Body);
        Assert.DoesNotContain('\t', harness.Notifications.Items.Single().Body);
        Assert.DoesNotContain('\u202E', harness.Notifications.Items.Single().Body);
    }

    [Fact]
    public async Task LongName_IsBoundedWithoutSplittingEmoji()
    {
        var name = string.Concat(Enumerable.Repeat("\ud83d\ude80", NotificationPresentationSanitizer.MaximumTextElements + 20));
        var server = TestData.LinuxServer() with { Name = name };
        await using var harness = await Harness.CreateAsync(ServerHealth.Healthy, server);

        harness.SetHealth(ServerHealth.Offline);
        await harness.Notifications.WaitForCountAsync(1);

        var body = Assert.Single(harness.Notifications.Items).Body;
        Assert.Equal(NotificationPresentationSanitizer.MaximumTextElements, body.Count(character => char.IsHighSurrogate(character)));
        Assert.DoesNotContain('\uFFFD', body);
    }

    [Fact]
    public void IllFormedUtf16Name_UsesSafeFallback()
    {
        var result = NotificationPresentationSanitizer.SanitizeServerName("broken\uD800", "Server");

        Assert.Equal("Server", result);
    }

    [Fact]
    public void AbsurdlyLongName_IsBoundedBeforeUnicodeNormalization()
    {
        var value = new string('a', 1_000_000);

        var result = NotificationPresentationSanitizer.SanitizeServerName(value, "Server");

        Assert.Equal(NotificationPresentationSanitizer.MaximumTextElements, result.Length);
    }

    [Fact]
    public async Task RepeatedStartStop_IsIdempotent_AndStopFencesLaterCallbacks()
    {
        var harness = await Harness.CreateAsync(ServerHealth.Healthy);
        await harness.Coordinator.StartAsync(CancellationToken.None);

        await harness.Coordinator.StopAsync(CancellationToken.None);
        await harness.Coordinator.StopAsync(CancellationToken.None);
        harness.SetHealth(ServerHealth.Offline);

        Assert.Empty(harness.Notifications.Items);
        await harness.DisposeAsync();
    }

    [Fact]
    public async Task BeginShutdown_SynchronouslyFencesTransitionAndPlatformDelivery()
    {
        await using var harness = await Harness.CreateAsync(ServerHealth.Healthy);

        harness.Coordinator.BeginShutdown();
        harness.SetHealth(ServerHealth.Offline);
        await harness.Coordinator.FlushAsync();

        Assert.Empty(harness.Notifications.Items);
        Assert.Equal(1, harness.Notifications.BeginShutdownCount);
    }

    [Fact]
    public async Task StopDuringNotificationCallback_CancelsAndDrainsWorker()
    {
        var server = TestData.LinuxServer();
        var states = new ServerMonitoringStateStore();
        var servers = new FakeServerService();
        servers.Servers.Add(server);
        var settings = new FakeNotificationSettingsService();
        var notifications = new BlockingNotificationService();
        var coordinator = new ServerAlertCoordinator(
            states,
            servers,
            settings,
            notifications,
            new FakeLocalizationService(),
            NullLogger<ServerAlertCoordinator>.Instance);
        states.Set(ServerMonitoringState.Initial(server.Id) with { Health = ServerHealth.Healthy });
        await coordinator.StartAsync(CancellationToken.None);
        states.Set(states.Get(server.Id) with { Health = ServerHealth.Warning });
        await notifications.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.StopAsync(CancellationToken.None);

        Assert.True(notifications.Cancelled.Task.IsCompletedSuccessfully);
        await coordinator.DisposeAsync();
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(Server server)
        {
            Server = server;
            Servers.Servers.Add(server);
            Coordinator = new ServerAlertCoordinator(
                States,
                Servers,
                Settings,
                Notifications,
                new FakeLocalizationService(),
                NullLogger<ServerAlertCoordinator>.Instance,
                Time);
        }

        public Server Server { get; }
        public ServerMonitoringStateStore States { get; } = new();
        public FakeServerService Servers { get; } = new();
        public FakeNotificationSettingsService Settings { get; } = new();
        public RecordingNotificationService Notifications { get; } = new();
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero));
        public ServerAlertCoordinator Coordinator { get; }

        public static async Task<Harness> CreateAsync(ServerHealth? initial = null, Server? server = null)
        {
            var harness = new Harness(server ?? TestData.LinuxServer());
            if (initial is not null)
            {
                harness.States.Set(ServerMonitoringState.Initial(harness.Server.Id) with { Health = initial.Value });
            }

            await harness.Coordinator.StartAsync(CancellationToken.None);
            return harness;
        }

        public void SetHealth(ServerHealth health, bool isRefreshing = false) =>
            States.Set(States.Get(Server.Id) with { Health = health, IsRefreshing = isRefreshing });

        public async ValueTask DisposeAsync() => await Coordinator.DisposeAsync();
    }

    private sealed class FakeNotificationSettingsService : INotificationSettingsService
    {
        public event EventHandler? NotificationsEnabledChanged;

        public bool NotificationsEnabled { get; private set; } = true;

        public void SetNotificationsEnabled(bool enabled)
        {
            if (NotificationsEnabled == enabled)
            {
                return;
            }

            NotificationsEnabled = enabled;
            NotificationsEnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class RecordingNotificationService : IUserNotificationService
    {
        private readonly ConcurrentQueue<UserNotification> _items = new();
        private readonly SemaphoreSlim _calls = new(0);

        public IReadOnlyList<UserNotification> Items => _items.ToArray();

        public int BeginShutdownCount { get; private set; }

        public void BeginShutdown() => BeginShutdownCount++;

        public Task ShowAsync(UserNotification notification, CancellationToken cancellationToken = default)
        {
            _items.Enqueue(notification);
            _calls.Release();
            return Task.CompletedTask;
        }

        public async Task WaitForCountAsync(int expected)
        {
            while (_items.Count < expected)
            {
                Assert.True(await _calls.WaitAsync(TimeSpan.FromSeconds(2)));
            }
        }
    }

    private sealed class BlockingNotificationService : IUserNotificationService
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ShowAsync(UserNotification notification, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }
}
