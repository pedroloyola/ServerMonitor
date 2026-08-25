using System.Globalization;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// The card reads metric values from the metrics store and health/refresh/stale/error from the
/// engine-owned <see cref="ServerMonitoringState"/>, pushed in via <see cref="ServerCardViewModel.ApplyMonitoringState"/>.
/// A manual refresh is delegated to <see cref="IMonitoringEngine.RefreshNowAsync"/>. These tests
/// drive those two paths directly, so they stay deterministic without any timer or scheduler.
/// </summary>
public sealed class ServerCardViewModelTests
{
    private static readonly Func<Task> NoOp = () => Task.CompletedTask;

    private static ServerCardViewModel CreateViewModel(
        FakeServerMetricsStore store,
        Server? server = null,
        SshConnectionResult? connection = null,
        FakeConnectionStateStore? connectionStore = null,
        IServerMonitoringStateStore? monitoringStore = null,
        FakeMonitoringEngine? engine = null) =>
        new(
            server ?? TestData.LinuxServer(),
            connection,
            new FakeLocalizationService(),
            store,
            connectionStore ?? new FakeConnectionStateStore(),
            monitoringStore ?? new ServerMonitoringStateStore(),
            engine ?? new FakeMonitoringEngine(),
            NoOp,
            NoOp,
            NoOp);

    private static IDisposable InvariantCulture() => new CultureScope();

    private static ServerMonitoringState State(
        Guid serverId,
        ServerHealth health = ServerHealth.Healthy,
        bool isRefreshing = false,
        bool isStale = false,
        int consecutiveFailures = 0,
        MetricsCollectionErrorCode? lastError = null,
        DateTimeOffset? lastAttemptAt = null,
        DateTimeOffset? lastSuccessAt = null) => new()
    {
        ServerId = serverId,
        Health = health,
        IsRefreshing = isRefreshing,
        IsStale = isStale,
        ConsecutiveFailures = consecutiveFailures,
        LastError = lastError,
        LastAttemptAt = lastAttemptAt,
        LastSuccessAt = lastSuccessAt
    };

    // --- Presentation states -------------------------------------------------

    [Fact]
    public void LinuxServerWithNoSnapshot_IsWaitingForData()
    {
        var vm = CreateViewModel(new FakeServerMetricsStore());

        Assert.True(vm.SupportsMetrics);
        Assert.False(vm.HasMetrics);
        Assert.True(vm.IsMetricsPending);
        Assert.False(vm.HasMetricsError);
        Assert.Equal(ServerHealth.Unknown, vm.Health);
    }

    [Fact]
    public void MacOsServerWithNoSnapshot_IsWaitingForData()
    {
        var vm = CreateViewModel(
            new FakeServerMetricsStore(),
            TestData.LinuxServer() with { OperatingSystem = ServerOperatingSystem.MacOS });

        Assert.True(vm.SupportsMetrics);
        Assert.False(vm.HasMetrics);
        Assert.True(vm.IsMetricsPending);
    }

    [Fact]
    public void UnsupportedOsServer_DoesNotSupportMetricsAndIsNotPending()
    {
        var vm = CreateViewModel(
            new FakeServerMetricsStore(),
            TestData.LinuxServer() with { OperatingSystem = ServerOperatingSystem.Unknown });

        Assert.False(vm.SupportsMetrics);
        Assert.False(vm.IsMetricsPending);
    }

    [Fact]
    public void InitialHealth_IsReadFromMonitoringStore()
    {
        var server = TestData.LinuxServer();
        var store = new ServerMonitoringStateStore();
        store.Set(State(server.Id, ServerHealth.Warning));
        var vm = CreateViewModel(new FakeServerMetricsStore(), server, monitoringStore: store);

        Assert.Equal(ServerHealth.Warning, vm.Health);
    }

    [Fact]
    public void RefreshCommand_IsEnabledInitially()
    {
        var vm = CreateViewModel(new FakeServerMetricsStore());

        Assert.True(vm.RefreshMetricsCommand.CanExecute(null));
    }

    // --- Monitoring state application (auto refresh path) --------------------

    [Fact]
    public void ApplyMonitoringState_UpdatesHealthAndDisplayName()
    {
        var server = TestData.LinuxServer();
        var vm = CreateViewModel(new FakeServerMetricsStore(), server);

        vm.ApplyMonitoringState(State(server.Id, ServerHealth.Critical));

        Assert.Equal(ServerHealth.Critical, vm.Health);
        Assert.Equal("ServerHealthCritical", vm.HealthDisplayName);
    }

    [Fact]
    public void ApplyMonitoringState_RereadsSnapshotSoAutomaticCyclesSurfaceNewValues()
    {
        using var _ = InvariantCulture();
        var server = TestData.LinuxServer();
        var store = new FakeServerMetricsStore(); // ctor snapshot is null
        var vm = CreateViewModel(store, server);
        Assert.False(vm.HasMetrics);

        // The engine stored a fresh snapshot; the state change makes the card re-read it.
        store.InitialSnapshot = TestData.Snapshot(server.Id, cpu: 42);
        vm.ApplyMonitoringState(State(server.Id, ServerHealth.Healthy));

        Assert.True(vm.HasMetrics);
        Assert.Equal("42%", vm.CpuUsageDisplay);
    }

    [Fact]
    public void WhileRefreshing_IsRefreshingAndCommandDisabled()
    {
        var server = TestData.LinuxServer();
        var vm = CreateViewModel(new FakeServerMetricsStore(), server);

        vm.ApplyMonitoringState(State(server.Id, isRefreshing: true));

        Assert.True(vm.IsRefreshingMetrics);
        Assert.False(vm.IsMetricsPending);
        Assert.False(vm.RefreshMetricsCommand.CanExecute(null));
    }

    [Fact]
    public void ApplyMonitoringState_RaisesPropertyChangedForHealthAndMetricValues()
    {
        using var _ = InvariantCulture();
        var server = TestData.LinuxServer();
        var store = new FakeServerMetricsStore();
        var vm = CreateViewModel(store, server);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        store.InitialSnapshot = TestData.Snapshot(server.Id, cpu: 5);
        vm.ApplyMonitoringState(State(server.Id, ServerHealth.Healthy));

        Assert.Contains(nameof(vm.Health), changed);
        Assert.Contains(nameof(vm.CpuUsageDisplay), changed);
        Assert.Contains(nameof(vm.IsRefreshingMetrics), changed);
    }

    // --- Stale / error surfacing --------------------------------------------

    [Fact]
    public void FailureWithExistingSnapshot_KeepsMetricsAndShowsStaleIndicator()
    {
        var server = TestData.LinuxServer();
        var store = new FakeServerMetricsStore { InitialSnapshot = TestData.Snapshot(server.Id, cpu: 20) };
        var vm = CreateViewModel(store, server);
        var success = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        vm.ApplyMonitoringState(State(
            server.Id,
            health: ServerHealth.Offline,
            isStale: true,
            consecutiveFailures: 1,
            lastError: MetricsCollectionErrorCode.TimedOut,
            lastSuccessAt: success,
            lastAttemptAt: success.AddMinutes(3)));

        Assert.True(vm.HasMetrics); // stale metrics stay visible
        Assert.True(vm.HasCpuUsage);
        Assert.True(vm.IsStale);
        Assert.True(vm.HasStaleIndicator);
        Assert.Equal("Last updated 3 min ago", vm.StaleAgeDisplay);
        Assert.False(vm.HasMetricsError); // no big error while a snapshot is shown
    }

    [Fact]
    public void FailureWithNoSnapshot_SurfacesErrorNotStale()
    {
        var server = TestData.LinuxServer();
        var vm = CreateViewModel(new FakeServerMetricsStore(), server);

        vm.ApplyMonitoringState(State(
            server.Id,
            health: ServerHealth.Offline,
            consecutiveFailures: 2,
            lastError: MetricsCollectionErrorCode.ConnectionFailed));

        Assert.False(vm.HasMetrics);
        Assert.True(vm.HasMetricsError);
        Assert.False(string.IsNullOrWhiteSpace(vm.MetricsErrorDisplay));
        Assert.False(vm.HasStaleIndicator);
    }

    [Fact]
    public void StaleAgeDisplay_UsesHoursBucketForLongGaps()
    {
        var server = TestData.LinuxServer();
        var store = new FakeServerMetricsStore { InitialSnapshot = TestData.Snapshot(server.Id, cpu: 20) };
        var vm = CreateViewModel(store, server);
        var success = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        vm.ApplyMonitoringState(State(
            server.Id,
            isStale: true,
            lastSuccessAt: success,
            lastAttemptAt: success.AddHours(2)));

        Assert.Equal("Last updated 2 h ago", vm.StaleAgeDisplay);
    }

    // --- Manual refresh delegates to the engine ------------------------------

    [Fact]
    public void ManualRefresh_DelegatesToEngineAndAppliesResultingState()
    {
        var server = TestData.LinuxServer();
        var store = new FakeServerMetricsStore();
        var monitoringStore = new ServerMonitoringStateStore();
        var engine = new FakeMonitoringEngine
        {
            OnRefresh = id =>
            {
                // Simulate the engine's cycle: store a snapshot and publish healthy state.
                store.InitialSnapshot = TestData.Snapshot(id, cpu: 7);
                monitoringStore.Set(State(id, ServerHealth.Healthy));
                return TestData.Success(TestData.Snapshot(id, cpu: 7));
            }
        };
        var vm = CreateViewModel(store, server, monitoringStore: monitoringStore, engine: engine);

        vm.RefreshMetricsCommand.Execute(null);

        Assert.Equal(1, engine.RefreshNowCount);
        Assert.Equal(server.Id, engine.LastRefreshedServerId);
        Assert.Equal(ServerHealth.Healthy, vm.Health);
        Assert.True(vm.HasMetrics);
    }

    [Fact]
    public void ManualRefresh_ConnectionResult_UpdatesConnectionStateAndStore()
    {
        var server = TestData.LinuxServer();
        var connectionStore = new FakeConnectionStateStore();
        var engine = new FakeMonitoringEngine
        {
            OnRefresh = id => TestData.Success(TestData.Snapshot(id, cpu: 1))
        };
        var vm = CreateViewModel(
            new FakeServerMetricsStore(),
            server,
            connectionStore: connectionStore,
            engine: engine);

        vm.RefreshMetricsCommand.Execute(null);

        Assert.Equal(ServerConnectionState.Connected, vm.ConnectionState);
        Assert.Equal(1, connectionStore.SetCount);
    }

    // --- Zero is data, null is unknown --------------------------------------

    [Fact]
    public void CpuZero_IsAvailableNotUnknown()
    {
        using var _ = InvariantCulture();
        var server = TestData.LinuxServer();
        var vm = CreateViewModel(new FakeServerMetricsStore
        {
            InitialSnapshot = TestData.Snapshot(server.Id, cpu: 0)
        }, server);

        Assert.True(vm.HasCpuUsage);
        Assert.Equal("0%", vm.CpuUsageDisplay);
    }

    [Fact]
    public void MemoryZero_IsAvailableNotUnknown()
    {
        using var _ = InvariantCulture();
        var server = TestData.LinuxServer();
        var vm = CreateViewModel(new FakeServerMetricsStore
        {
            InitialSnapshot = TestData.Snapshot(server.Id, memoryPercent: 0)
        }, server);

        Assert.True(vm.HasMemoryUsage);
        Assert.Equal("0%", vm.MemoryUsageDisplay);
    }

    [Fact]
    public void DiskZero_IsAvailableNotUnknown()
    {
        using var _ = InvariantCulture();
        var server = TestData.LinuxServer();
        var vm = CreateViewModel(new FakeServerMetricsStore
        {
            InitialSnapshot = TestData.Snapshot(server.Id, diskPercent: 0)
        }, server);

        Assert.True(vm.HasDiskUsage);
        Assert.Equal("0%", vm.DiskUsageDisplay);
    }

    [Fact]
    public void PartialMetrics_ShowsAvailableFieldsAndHidesUnknownOnes()
    {
        var server = TestData.LinuxServer();
        var vm = CreateViewModel(new FakeServerMetricsStore
        {
            InitialSnapshot = TestData.Snapshot(server.Id, cpu: 20, uptime: TimeSpan.FromHours(5))
        }, server);

        Assert.True(vm.HasCpuUsage);
        Assert.True(vm.HasUptime);
        Assert.False(vm.HasMemoryUsage);
        Assert.False(vm.HasDiskUsage);
        Assert.False(vm.HasDetectedOperatingSystem);
    }

    [Fact]
    public void SnapshotWithoutMetricValues_ProducesNullDisplaysButStillHasSnapshot()
    {
        var server = TestData.LinuxServer();
        var vm = CreateViewModel(new FakeServerMetricsStore
        {
            InitialSnapshot = TestData.Snapshot(server.Id, hostname: "web-01")
        }, server);

        Assert.True(vm.HasMetrics);
        Assert.Null(vm.CpuUsageDisplay);
        Assert.Null(vm.MemoryUsageDisplay);
        Assert.Null(vm.DiskUsageDisplay);
        Assert.Null(vm.UptimeDisplay);
        Assert.False(vm.HasCpuUsage);
    }

    // --- Timestamp / OS / uptime --------------------------------------------

    [Fact]
    public void Timestamp_IsPresentWhenSnapshotExists()
    {
        var server = TestData.LinuxServer();
        var withSnapshot = CreateViewModel(new FakeServerMetricsStore
        {
            InitialSnapshot = TestData.Snapshot(server.Id, cpu: 1)
        }, server);
        var withoutSnapshot = CreateViewModel(new FakeServerMetricsStore(), server);

        Assert.False(string.IsNullOrWhiteSpace(withSnapshot.MetricsTimestampDisplay));
        Assert.Null(withoutSnapshot.MetricsTimestampDisplay);
    }

    [Fact]
    public void DetectedOperatingSystem_CombinesNameAndVersionThroughLocalization()
    {
        var server = TestData.LinuxServer();
        var vm = CreateViewModel(new FakeServerMetricsStore
        {
            InitialSnapshot = TestData.Snapshot(server.Id, osName: "Ubuntu", osVersion: "22.04")
        }, server);

        Assert.True(vm.HasDetectedOperatingSystem);
        Assert.Equal("OS: Ubuntu 22.04", vm.DetectedOperatingSystemDisplay);
    }

    [Theory]
    [InlineData(50, 0, 0, "2d 2h")] // 50h -> 2 days 2 hours
    [InlineData(0, 5, 0, "5h 0m")]  // 5 hours
    [InlineData(0, 0, 7, "7m")]     // 7 minutes
    public void Uptime_UsesTheCorrectFormatBucket(int hours, int extraHours, int minutes, string expected)
    {
        using var _ = InvariantCulture();
        var server = TestData.LinuxServer();
        var uptime = TimeSpan.FromHours(hours + extraHours) + TimeSpan.FromMinutes(minutes);
        var vm = CreateViewModel(new FakeServerMetricsStore
        {
            InitialSnapshot = TestData.Snapshot(server.Id, uptime: uptime)
        }, server);

        Assert.Equal(expected, vm.UptimeDisplay);
    }

    // --- Formatting ----------------------------------------------------------

    [Fact]
    public void Memory_FallsBackToByteUsageWhenPercentUnavailable()
    {
        using var _ = InvariantCulture();
        var server = TestData.LinuxServer();
        var vm = CreateViewModel(new FakeServerMetricsStore
        {
            InitialSnapshot = TestData.Snapshot(
                server.Id,
                memoryPercent: null,
                memoryUsed: 8L * 1024 * 1024 * 1024,
                memoryTotal: 16L * 1024 * 1024 * 1024)
        }, server);

        Assert.True(vm.HasMemoryUsage);
        var display = vm.MemoryUsageDisplay!;
        Assert.Contains("GB", display);
        Assert.Contains("/", display);
    }

    [Fact]
    public void Disk_SubGigabyteUsageIsShownInMegabytes()
    {
        using var _ = InvariantCulture();
        var server = TestData.LinuxServer();
        var vm = CreateViewModel(new FakeServerMetricsStore
        {
            InitialSnapshot = TestData.Snapshot(
                server.Id,
                diskPercent: null,
                diskUsed: 200L * 1024 * 1024,
                diskTotal: 800L * 1024 * 1024)
        }, server);

        var display = vm.DiskUsageDisplay!;
        Assert.Contains("MB", display);
        Assert.Contains("/", display);
    }

    [Fact]
    public void MicroProgressBarValues_PreservePercentAndReportAvailability()
    {
        var server = TestData.LinuxServer();
        var vmWithMetrics = CreateViewModel(new FakeServerMetricsStore
        {
            InitialSnapshot = TestData.Snapshot(server.Id, cpu: 25.5, memoryPercent: 60.0, diskPercent: 80.0)
        }, server);

        Assert.True(vmWithMetrics.HasCpuPercent);
        Assert.Equal(25.5, vmWithMetrics.CpuUsageValue);
        Assert.True(vmWithMetrics.HasMemoryPercent);
        Assert.Equal(60.0, vmWithMetrics.MemoryUsageValue);
        Assert.True(vmWithMetrics.HasDiskPercent);
        Assert.Equal(80.0, vmWithMetrics.DiskUsageValue);

        var vmNullMetrics = CreateViewModel(new FakeServerMetricsStore
        {
            InitialSnapshot = TestData.Snapshot(server.Id, cpu: null, memoryPercent: null, diskPercent: null)
        }, server);

        Assert.False(vmNullMetrics.HasCpuPercent);
        Assert.Equal(0, vmNullMetrics.CpuUsageValue);
        Assert.False(vmNullMetrics.HasMemoryPercent);
        Assert.Equal(0, vmNullMetrics.MemoryUsageValue);
        Assert.False(vmNullMetrics.HasDiskPercent);
        Assert.Equal(0, vmNullMetrics.DiskUsageValue);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture;
        private readonly CultureInfo _uiCulture;

        public CultureScope()
        {
            _culture = CultureInfo.CurrentCulture;
            _uiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }
}
