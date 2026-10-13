using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.12 UI12A-STATE-04 (Prism, P2). History's "Atual" (and each chart's accessible summary) took the last snapshot with no
/// staleness check, so a server Offline for days showed a days-old value as current. Now a stale, Offline or failed
/// "Sem dados" snapshot reads "—" in "Atual" and the summary; a fresh one is unchanged.
/// </summary>
public sealed class Ui12HistoryCurrentValueTests
{
    private static readonly Guid Server = Guid.NewGuid();

    [Theory]
    [InlineData(ServerHealth.Offline, false, false)]
    [InlineData(ServerHealth.Healthy, true, false)]   // engine-stale
    [InlineData(ServerHealth.Unknown, false, true)]   // failed "Sem dados"
    public void ARetainedNonCurrentSnapshot_IsNotShownAsCurrent(ServerHealth health, bool stale, bool failed)
    {
        var vm = NewViewModel(health, stale, failed);

        Assert.False(vm.HasCpuCurrent);
        Assert.Equal("—", vm.CpuCurrentValue);
        Assert.DoesNotContain("91", vm.CpuSummary, StringComparison.Ordinal);
        Assert.False(vm.HasMemoryCurrent);
        Assert.False(vm.HasDiskCurrent);
    }

    [Fact]
    public void AFreshSnapshot_IsTheCurrentValue()
    {
        var vm = NewViewModel(ServerHealth.Healthy, stale: false, failed: false);

        Assert.True(vm.HasCpuCurrent);
        Assert.Equal("91", vm.CpuCurrentValue);
    }

    [Fact]
    public void TheRule_KeepsTheSnapshotWhenNoEngineStateIsKnown()
    {
        var snapshot = new ServerMetricsSnapshot { ServerId = Server, CollectedAt = DateTimeOffset.UtcNow, CpuUsagePercent = 91 };
        Assert.Same(snapshot, HistoryViewModel.CurrentSnapshot(snapshot, null));
        Assert.Null(HistoryViewModel.CurrentSnapshot(null, ServerMonitoringState.Initial(Server)));
    }

    private static HistoryViewModel NewViewModel(ServerHealth health, bool stale, bool failed)
    {
        var metrics = new FakeServerMetricsStore
        {
            InitialSnapshot = new ServerMetricsSnapshot
            {
                ServerId = Server,
                CollectedAt = DateTimeOffset.UtcNow.AddDays(-3),
                CpuUsagePercent = 91,
                MemoryUsagePercent = 40,
                DiskUsagePercent = 50,
            },
        };
        var states = new ServerMonitoringStateStore();
        states.Set(ServerMonitoringState.Initial(Server) with
        {
            Health = health,
            IsStale = stale,
            LastError = failed ? MetricsCollectionErrorCode.NoMetricsAvailable : null,
        });
        var vm = new HistoryViewModel(
            new EmptyHistory(),
            metrics,
            states,
            new FakeServerService(),
            new FakeNavigationService(),
            new ResWLocalizationService("pt-PT"),
            NullLogger<HistoryViewModel>.Instance);
        vm.Load(Server, "db");
        return vm;
    }

    /// <summary>"Atual" never comes from the history query; a Release-safe empty one (the Qa double is Debug-only).</summary>
    private sealed class EmptyHistory : IServerHistoryQueryService
    {
        public bool IsAvailable => true;

        public Task<ServerHistoryResult> GetHistoryAsync(Guid serverId, HistoryTimeRange range, CancellationToken cancellationToken = default)
        {
            var end = DateTimeOffset.UtcNow;
            return Task.FromResult(ServerHistoryResult.Empty(serverId, range, end - range.ToDuration(), end));
        }
    }
}
