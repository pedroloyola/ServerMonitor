using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.12 C-E1 (Cortex UI.12A, P2, unknown ≠ zero). <c>SqliteServerHistoryStore.QueryAsync</c> caught every query failure and
/// returned an empty list, so History received "no samples" - and the 30-day probe too - and told the user nothing had ever
/// been recorded, when in fact the read had failed. End to end over the REAL store (a temp database, never the user's), the
/// REAL query service and the view model: a failing query now shows "history unavailable", never an empty state.
/// </summary>
public sealed class Ui12HistoryQueryFailureTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "sm-ui12-history", $"{Guid.NewGuid():N}.db");

    [Fact]
    public async Task ATransientQueryFailure_ShowsUnavailable_NotNeverRecorded()
    {
        var (store, serverId) = await StoreWithOneSample();
        await Execute("DROP TABLE history_samples;"); // the next SELECT fails (SQLITE_ERROR, not fatal)

        var vm = NewViewModel(store);
        vm.Load(serverId, "server");
        await vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.True(vm.IsUnavailable);
        Assert.False(vm.IsEmpty);
        Assert.Equal(HistoryEmptyKind.None, vm.EmptyKind);
        Assert.True(store.IsAvailable); // transient: availability does not flap
    }

    [Fact]
    public async Task TheStore_ThrowsOnAFailedQuery_InsteadOfReturningNoSamples()
    {
        var (store, serverId) = await StoreWithOneSample();
        await Execute("DROP TABLE history_samples;");

        await Assert.ThrowsAsync<SqliteException>(() =>
            store.QueryAsync(serverId, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public async Task AWorkingStore_StillReportsAGenuinelyEmptyPeriod_AsEmpty()
    {
        var (store, _) = await StoreWithOneSample();

        var vm = NewViewModel(store);
        vm.Load(Guid.NewGuid(), "other server"); // nothing stored for it
        await vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.False(vm.IsUnavailable);
        Assert.True(vm.IsEmpty);
        Assert.Equal(HistoryEmptyKind.NeverRecorded, vm.EmptyKind);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(SqliteServerHistoryStore Store, Guid ServerId)> StoreWithOneSample()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        var store = new SqliteServerHistoryStore(
            new HistoryStorageOptions { DatabasePath = _dbPath },
            NullLogger<SqliteServerHistoryStore>.Instance);
        await store.InitializeAsync();
        Assert.True(store.IsAvailable);

        var serverId = Guid.NewGuid();
        await store.WriteAsync([
            new ServerHistorySample
            {
                ServerId = serverId,
                CapturedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                Health = ServerHealth.Healthy,
                CpuPercent = 10,
                MemoryPercent = 20,
                DiskPercent = 30,
            },
        ]);
        return (store, serverId);
    }

    private async Task Execute(string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static HistoryViewModel NewViewModel(IServerHistoryStore store) => new(
        new ServerHistoryQueryService(store, NullLogger<ServerHistoryQueryService>.Instance),
        new FakeServerMetricsStore(),
        new ServerMonitoringStateStore(),
        new FakeServerService(),
        new FakeNavigationService(),
        new FakeLocalizationService(),
        NullLogger<HistoryViewModel>.Instance);
}
