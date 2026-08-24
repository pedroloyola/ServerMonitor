using System.Globalization;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

public sealed class ServerCardViewModelTests
{
    private static readonly Func<Task> NoOp = () => Task.CompletedTask;

    private static ServerCardViewModel CreateViewModel(
        FakeServerMetricsStore store,
        Server? server = null,
        SshConnectionResult? connection = null,
        FakeConnectionStateStore? connectionStore = null) =>
        new(
            server ?? TestData.LinuxServer(),
            connection,
            new FakeLocalizationService(),
            store,
            connectionStore ?? new FakeConnectionStateStore(),
            NoOp,
            NoOp,
            NoOp);

    // The refresh command is async void. Rather than poll (which races on the
    // scheduler), drive it on a single-threaded pump: ConfigureAwait(true)
    // continuations post here and RunUntilIdle drains them in order on the
    // test thread, so state transitions are fully deterministic.
    private static void WithPump(Action<PumpSynchronizationContext> body)
    {
        var previous = SynchronizationContext.Current;
        var pump = new PumpSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            body(pump);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    // Formatting is culture-sensitive by design (separators, digits). Pin the
    // invariant culture so numeric assertions are deterministic while still
    // exercising the real localization/formatting path.
    private static IDisposable InvariantCulture() => new CultureScope();

    // --- Presentation states -------------------------------------------------

    [Fact]
    public void LinuxServerWithNoSnapshot_IsWaitingForData()
    {
        var vm = CreateViewModel(new FakeServerMetricsStore());

        Assert.True(vm.SupportsMetrics);
        Assert.False(vm.HasMetrics);
        Assert.True(vm.IsMetricsPending);
        Assert.False(vm.HasMetricsError);
    }

    [Fact]
    public void NonLinuxServer_DoesNotSupportMetricsAndIsNotPending()
    {
        var vm = CreateViewModel(
            new FakeServerMetricsStore(),
            TestData.LinuxServer() with { OperatingSystem = ServerOperatingSystem.MacOS });

        Assert.False(vm.SupportsMetrics);
        Assert.False(vm.IsMetricsPending);
    }

    [Fact]
    public void RefreshCommand_IsEnabledInitially()
    {
        var vm = CreateViewModel(new FakeServerMetricsStore());

        Assert.True(vm.RefreshMetricsCommand.CanExecute(null));
    }

    [Fact]
    public void DuringRefresh_IsRefreshingAndCommandDisabled()
    {
        var server = TestData.LinuxServer();
        var store = new FakeServerMetricsStore();
        var gate = store.Gate();
        var vm = CreateViewModel(store, server);

        WithPump(pump =>
        {
            vm.RefreshMetricsCommand.Execute(null);

            Assert.True(vm.IsRefreshingMetrics);
            Assert.False(vm.IsMetricsPending);
            Assert.False(vm.RefreshMetricsCommand.CanExecute(null));

            gate.SetResult(TestData.Success(TestData.Snapshot(server.Id, cpu: 1)));
            pump.RunUntilIdle();
        });

        Assert.False(vm.IsRefreshingMetrics);
    }

    [Fact]
    public void AfterSuccess_SnapshotAppliedAndCommandReenabled()
    {
        using var _ = InvariantCulture();
        var server = TestData.LinuxServer();
        var store = new FakeServerMetricsStore();
        var gate = store.Gate();
        var vm = CreateViewModel(store, server);

        WithPump(pump =>
        {
            vm.RefreshMetricsCommand.Execute(null);
            gate.SetResult(TestData.Success(TestData.Snapshot(server.Id, cpu: 42)));
            pump.RunUntilIdle();
        });

        Assert.True(vm.HasMetrics);
        Assert.Equal("42%", vm.CpuUsageDisplay);
        Assert.False(vm.HasMetricsError);
        Assert.True(vm.RefreshMetricsCommand.CanExecute(null));
    }

    [Fact]
    public void AfterError_ErrorShownAndCommandReenabled()
    {
        var store = new FakeServerMetricsStore();
        var gate = store.Gate();
        var vm = CreateViewModel(store);

        WithPump(pump =>
        {
            vm.RefreshMetricsCommand.Execute(null);
            gate.SetResult(TestData.Failure(MetricsCollectionErrorCode.ConnectionFailed));
            pump.RunUntilIdle();
        });

        Assert.True(vm.HasMetricsError);
        Assert.False(string.IsNullOrWhiteSpace(vm.MetricsErrorDisplay));
        Assert.True(vm.RefreshMetricsCommand.CanExecute(null));
    }

    [Fact]
    public void Cancellation_SurfacesAsErrorAndReenablesCommand()
    {
        var store = new FakeServerMetricsStore();
        var gate = store.Gate();
        var vm = CreateViewModel(store);

        WithPump(pump =>
        {
            vm.RefreshMetricsCommand.Execute(null);
            gate.SetResult(TestData.Failure(MetricsCollectionErrorCode.Cancelled));
            pump.RunUntilIdle();
        });

        Assert.True(vm.HasMetricsError);
        Assert.True(vm.RefreshMetricsCommand.CanExecute(null));
    }

    [Fact]
    public void Refresh_ConnectionResult_UpdatesConnectionStateAndStore()
    {
        var server = TestData.LinuxServer();
        var connectionStore = new FakeConnectionStateStore();
        var store = new FakeServerMetricsStore
        {
            NextResult = TestData.Success(TestData.Snapshot(server.Id, cpu: 1))
        };
        var vm = CreateViewModel(store, server, connectionStore: connectionStore);

        // A completed store result runs the command synchronously; no pump needed.
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

    // --- Snapshot update & stale-data-after-failure --------------------------

    [Fact]
    public void Refresh_UpdatesDisplayedValuesFromNewSnapshot()
    {
        using var _ = InvariantCulture();
        var server = TestData.LinuxServer();
        var store = new FakeServerMetricsStore
        {
            InitialSnapshot = TestData.Snapshot(server.Id, cpu: 10),
            NextResult = TestData.Success(TestData.Snapshot(server.Id, cpu: 55))
        };
        var vm = CreateViewModel(store, server);
        Assert.Equal("10%", vm.CpuUsageDisplay);

        vm.RefreshMetricsCommand.Execute(null);

        Assert.Equal("55%", vm.CpuUsageDisplay);
    }

    [Fact]
    public void FailureAfterExistingSnapshot_KeepsStaleMetricsAndSurfacesError()
    {
        var server = TestData.LinuxServer();
        var store = new FakeServerMetricsStore
        {
            InitialSnapshot = TestData.Snapshot(server.Id, cpu: 20),
            NextResult = TestData.Failure(MetricsCollectionErrorCode.TimedOut)
        };
        var vm = CreateViewModel(store, server);

        vm.RefreshMetricsCommand.Execute(null);

        Assert.True(vm.HasMetrics);
        Assert.True(vm.HasCpuUsage);
        Assert.True(vm.HasMetricsError);
    }

    [Fact]
    public void Refresh_RaisesPropertyChangedForMetricValues()
    {
        var server = TestData.LinuxServer();
        var store = new FakeServerMetricsStore
        {
            NextResult = TestData.Success(TestData.Snapshot(server.Id, cpu: 5))
        };
        var vm = CreateViewModel(store, server);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        vm.RefreshMetricsCommand.Execute(null);

        Assert.Contains(nameof(vm.CpuUsageDisplay), changed);
        Assert.Contains(nameof(vm.IsRefreshingMetrics), changed);
    }

    private sealed class PumpSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void RunUntilIdle()
        {
            while (_queue.Count > 0)
            {
                var (callback, state) = _queue.Dequeue();
                callback(state);
            }
        }
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
