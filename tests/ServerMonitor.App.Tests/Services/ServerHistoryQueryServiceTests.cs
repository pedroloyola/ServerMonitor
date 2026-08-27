using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.Tests.Services;

public sealed class ServerHistoryQueryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static (ServerHistoryQueryService service, FakeServerHistoryStore store, FakeTimeProvider time) New()
    {
        var store = new FakeServerHistoryStore();
        var time = new FakeTimeProvider();
        time.SetUtcNow(Now);
        var service = new ServerHistoryQueryService(store, NullLogger<ServerHistoryQueryService>.Instance, time);
        return (service, store, time);
    }

    [Fact]
    public async Task UnavailableStore_ReturnsEmpty_AndIsUnavailable()
    {
        var (service, store, _) = New();
        store.Available = false;

        var result = await service.GetHistoryAsync(Guid.NewGuid(), HistoryTimeRange.Last24Hours);

        Assert.False(service.IsAvailable);
        Assert.True(result.IsEmpty);
        Assert.Equal(TimeSpan.FromHours(24), result.EndUtc - result.StartUtc);
    }

    [Fact]
    public async Task RangeBounds_ComputedFromTimeProvider()
    {
        var (service, _, _) = New();

        var result = await service.GetHistoryAsync(Guid.NewGuid(), HistoryTimeRange.Last6Hours);

        Assert.Equal(Now, result.EndUtc);
        Assert.Equal(Now - TimeSpan.FromHours(6), result.StartUtc);
    }

    [Fact]
    public async Task AvailableWithSamples_ReturnsDownsampledSeries()
    {
        var (service, store, _) = New();
        var id = Guid.NewGuid();
        store.QueryFactory = (_, start, _) =>
            Enumerable.Range(0, 20).Select(i => new ServerHistorySample
            {
                ServerId = id,
                CapturedAtUtc = start + TimeSpan.FromMinutes(i),
                Health = ServerHealth.Healthy,
                CpuPercent = i,
                MemoryPercent = i * 2,
                DiskPercent = i * 3
            }).ToList();

        var result = await service.GetHistoryAsync(id, HistoryTimeRange.LastHour);

        Assert.False(result.IsEmpty);
        Assert.True(result.Cpu.HasData);
        Assert.Equal(19, result.Cpu.Maximum);
        Assert.Equal(57, result.Disk.Maximum);
    }

    [Fact]
    public async Task CancellationRequested_Throws()
    {
        var (service, store, _) = New();
        store.QueryFactory = (_, _, _) => Array.Empty<ServerHistorySample>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetHistoryAsync(Guid.NewGuid(), HistoryTimeRange.Last30Days, cts.Token));
    }

    [Fact]
    public async Task OfflineSamples_AreReportedToTheUi()
    {
        var (service, store, _) = New();
        var id = Guid.NewGuid();
        store.QueryFactory = (_, start, _) =>
        [
            new ServerHistorySample
            {
                ServerId = id,
                CapturedAtUtc = start,
                Health = ServerHealth.Offline
            }
        ];

        var result = await service.GetHistoryAsync(id, HistoryTimeRange.LastHour);

        Assert.True(result.ContainsOfflineSamples);
        Assert.False(result.IsEmpty);
    }
}
