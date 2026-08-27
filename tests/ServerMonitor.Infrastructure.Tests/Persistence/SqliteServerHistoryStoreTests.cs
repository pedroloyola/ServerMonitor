using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure.Tests.Persistence;

public sealed class SqliteServerHistoryStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly List<SqliteServerHistoryStore> _stores = [];

    public SqliteServerHistoryStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "sm-history-tests", $"{Guid.NewGuid():N}.db");
    }

    private SqliteServerHistoryStore NewStore(
        TimeSpan? retention = null,
        int? maxQueryRows = null,
        Action? queryReaderOpened = null)
    {
        var options = new HistoryStorageOptions
        {
            DatabasePath = _dbPath,
            RetentionPeriod = retention ?? TimeSpan.FromDays(30),
            MaxQueryRows = maxQueryRows ?? HistoryStorageOptions.DefaultMaxQueryRows,
            QueryReaderOpenedForTesting = queryReaderOpened
        };
        var store = new SqliteServerHistoryStore(options, NullLogger<SqliteServerHistoryStore>.Instance);
        _stores.Add(store);
        return store;
    }

    private static ServerHistorySample Sample(
        Guid serverId,
        DateTimeOffset at,
        ServerHealth health = ServerHealth.Healthy,
        double? cpu = 10,
        double? mem = 20,
        double? disk = 30) => new()
    {
        ServerId = serverId,
        CapturedAtUtc = at,
        Health = health,
        CpuPercent = cpu,
        MemoryPercent = mem,
        DiskPercent = disk
    };

    [Fact]
    public async Task Initialize_FreshDatabase_IsAvailableAndUsable()
    {
        var store = NewStore();
        await store.InitializeAsync();

        Assert.True(store.IsAvailable);
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task WriteThenQuery_RoundTripsUtcAndValues()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var serverId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 8, 26, 10, 0, 0, 123, TimeSpan.Zero);
        await store.WriteAsync([Sample(serverId, at, ServerHealth.Warning, cpu: 44.5, mem: 55.5, disk: 66.5)]);

        var rows = await store.QueryAsync(serverId, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1));

        var row = Assert.Single(rows);
        Assert.Equal(at.ToUnixTimeMilliseconds(), row.CapturedAtUtc.ToUnixTimeMilliseconds());
        Assert.Equal(ServerHealth.Warning, row.Health);
        Assert.Equal(44.5, row.CpuPercent);
        Assert.Equal(55.5, row.MemoryPercent);
        Assert.Equal(66.5, row.DiskPercent);
    }

    [Fact]
    public async Task NullMetrics_PersistedAsNull_NotZero()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var serverId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await store.WriteAsync([Sample(serverId, at, ServerHealth.Offline, cpu: null, mem: null, disk: null)]);

        var row = Assert.Single(await store.QueryAsync(serverId, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1)));
        Assert.Null(row.CpuPercent);
        Assert.Null(row.MemoryPercent);
        Assert.Null(row.DiskPercent);
        Assert.Equal(ServerHealth.Offline, row.Health);
    }

    [Fact]
    public async Task DuplicateServerTimestamp_IsIdempotent()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var serverId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await store.WriteAsync([Sample(serverId, at, cpu: 10)]);
        await store.WriteAsync([Sample(serverId, at, cpu: 99)]); // same key → ignored

        var rows = await store.QueryAsync(serverId, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1));
        var row = Assert.Single(rows);
        Assert.Equal(10, row.CpuPercent); // first write wins (INSERT OR IGNORE)
    }

    [Fact]
    public async Task Query_RespectsRangeBounds_AndAscendingOrder()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var serverId = Guid.NewGuid();
        var baseAt = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        await store.WriteAsync(
        [
            Sample(serverId, baseAt),
            Sample(serverId, baseAt + TimeSpan.FromMinutes(10)),
            Sample(serverId, baseAt + TimeSpan.FromMinutes(20)),
            Sample(serverId, baseAt + TimeSpan.FromMinutes(40))
        ]);

        var rows = await store.QueryAsync(serverId, baseAt + TimeSpan.FromMinutes(5), baseAt + TimeSpan.FromMinutes(25));

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].CapturedAtUtc < rows[1].CapturedAtUtc);
    }

    [Fact]
    public async Task Query_DoesNotLeakOtherServers()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await store.WriteAsync([Sample(a, at), Sample(b, at)]);

        var rows = await store.QueryAsync(a, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1));
        Assert.All(rows, r => Assert.Equal(a, r.ServerId));
        Assert.Single(rows);
    }

    [Fact]
    public async Task Retention_RemovesOlderThanCutoff_KeepsBoundary()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var serverId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        var cutoff = now - TimeSpan.FromDays(30);
        await store.WriteAsync(
        [
            Sample(serverId, cutoff - TimeSpan.FromSeconds(1)), // older → removed
            Sample(serverId, cutoff),                            // exactly at cutoff → kept (< cutoff)
            Sample(serverId, now)                                // newer → kept
        ]);

        var removed = await store.DeleteOlderThanAsync(cutoff);

        Assert.Equal(1, removed);
        var rows = await store.QueryAsync(serverId, cutoff - TimeSpan.FromDays(1), now + TimeSpan.FromDays(1));
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task Clear_RemovesAllRows_SchemaStillUsable()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var serverId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await store.WriteAsync([Sample(serverId, at)]);

        Assert.True(await store.ClearAsync());

        Assert.True(store.IsAvailable);
        Assert.Empty(await store.QueryAsync(serverId, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1)));
        // Still writable after clear.
        await store.WriteAsync([Sample(serverId, at)]);
        Assert.Single(await store.QueryAsync(serverId, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Restart_PreservesData_AndReInitializeIsNoOp()
    {
        var serverId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

        var first = NewStore();
        await first.InitializeAsync();
        await first.WriteAsync([Sample(serverId, at, cpu: 77)]);
        first.Dispose();
        SqliteConnection.ClearAllPools();

        var second = NewStore();
        await second.InitializeAsync(); // existing DB → migration no-op
        Assert.True(second.IsAvailable);
        var row = Assert.Single(await second.QueryAsync(serverId, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1)));
        Assert.Equal(77, row.CpuPercent);
    }

    [Fact]
    public async Task Version1Migration_PreservesRows_AndSanitizesMalformedMetrics()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        var serverId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE history_samples (server_id TEXT NOT NULL, captured_at_utc INTEGER NOT NULL, " +
                "health INTEGER NOT NULL, cpu_percent REAL NULL, memory_percent REAL NULL, disk_percent REAL NULL, " +
                "PRIMARY KEY (server_id, captured_at_utc));" +
                "PRAGMA user_version=1;" +
                "INSERT INTO history_samples VALUES ($id, $at, 2, 55.5, 999, 'malformed');";
            command.Parameters.AddWithValue("$id", serverId.ToString());
            command.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync();
        }

        var store = NewStore();
        await store.InitializeAsync();

        Assert.True(store.IsAvailable);
        var row = Assert.Single(await store.QueryAsync(serverId, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1)));
        Assert.Equal(55.5, row.CpuPercent);
        Assert.Null(row.MemoryPercent);
        Assert.Null(row.DiskPercent);
        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await verify.OpenAsync();
        await using var version = verify.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(2L, await version.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Read_DefensivelySanitizesValues_WhenChecksWereBypassed()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var serverId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "PRAGMA ignore_check_constraints=ON;" +
                "INSERT INTO history_samples VALUES ($id, $at, 999, -10, 101, 'NaN');";
            command.Parameters.AddWithValue("$id", serverId.ToString());
            command.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync();
        }

        var row = Assert.Single(await store.QueryAsync(serverId, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1)));
        Assert.Equal(ServerHealth.Unknown, row.Health);
        Assert.Null(row.CpuPercent);
        Assert.Null(row.MemoryPercent);
        Assert.Null(row.DiskPercent);
    }

    [Fact]
    public async Task SchemaChecks_RejectOutOfRangeDirectWrites()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO history_samples VALUES ($id, $at, 1, 101, NULL, NULL);";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var exception = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(19, exception.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    [Fact]
    public async Task CurrentVersionWithIncompatibleSchema_IsUnavailable_AndPreserved()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE wrong_table(value TEXT); PRAGMA user_version=2;";
            await command.ExecuteNonQueryAsync();
        }

        var store = NewStore();
        await store.InitializeAsync();

        Assert.False(store.IsAvailable);
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task Query_OverDefensiveLimit_ThrowsWithoutReturningPartialPrefix()
    {
        var store = NewStore(maxQueryRows: 3);
        await store.InitializeAsync();
        var serverId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        await store.WriteAsync(Enumerable.Range(0, 4)
            .Select(i => Sample(serverId, at + TimeSpan.FromSeconds(i)))
            .ToArray());

        await Assert.ThrowsAsync<HistoryQueryLimitExceededException>(() =>
            store.QueryAsync(serverId, at - TimeSpan.FromSeconds(1), at + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task BatchFailure_RollsBackEveryRow_AndNextBatchRecovers()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var serverId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString()))
        {
            await connection.OpenAsync();
            await using var trigger = connection.CreateCommand();
            trigger.CommandText =
                "CREATE TRIGGER fail_test_batch BEFORE INSERT ON history_samples " +
                "WHEN NEW.cpu_percent = 99 BEGIN SELECT RAISE(ABORT, 'test failure'); END;";
            await trigger.ExecuteNonQueryAsync();
        }

        await store.WriteAsync([
            Sample(serverId, at, cpu: 10),
            Sample(serverId, at + TimeSpan.FromSeconds(1), cpu: 99)
        ]);
        Assert.Empty(await store.QueryAsync(serverId, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1)));

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString()))
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER fail_test_batch;";
            await drop.ExecuteNonQueryAsync();
        }

        await store.WriteAsync([Sample(serverId, at, cpu: 25)]);
        Assert.Single(await store.QueryAsync(serverId, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task TransientStartupLock_IsRetryable_AndRecoversAfterLockRelease()
    {
        var first = NewStore();
        await first.InitializeAsync();
        first.Dispose();
        SqliteConnection.ClearAllPools();

        await using var locker = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString());
        await locker.OpenAsync();
        await using (var lockCommand = locker.CreateCommand())
        {
            lockCommand.CommandText =
                "PRAGMA journal_mode=DELETE; PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE;";
            await lockCommand.ExecuteNonQueryAsync();
        }

        var second = NewStore();
        await second.InitializeAsync();
        Assert.False(second.IsAvailable);
        Assert.True(second.CanRetryInitialization);

        await using (var release = locker.CreateCommand())
        {
            release.CommandText = "ROLLBACK;";
            await release.ExecuteNonQueryAsync();
        }
        await locker.CloseAsync(); // locking_mode=EXCLUSIVE is held for the connection lifetime

        await second.InitializeAsync();
        Assert.True(second.IsAvailable);
        Assert.False(second.CanRetryInitialization);
    }

    [Fact]
    public async Task QueryCancellation_AfterRealReaderOpened_CompletesWithinBound()
    {
        var readerOpened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseReader = new ManualResetEventSlim();
        var store = NewStore(queryReaderOpened: () =>
        {
            readerOpened.TrySetResult(true);
            releaseReader.Wait();
        });
        await store.InitializeAsync();
        var serverId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.WriteAsync([Sample(serverId, now)]);

        using var cancellation = new CancellationTokenSource();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var query = Task.Run(() => store.QueryAsync(
            serverId,
            now - TimeSpan.FromHours(1),
            now + TimeSpan.FromHours(1),
            cancellation.Token));

        await readerOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        releaseReader.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await query);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Cancellation took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task ConcurrentQueriesShareReadGate_AndResetWaitsForBoth()
    {
        var openedCount = 0;
        var bothOpened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseReaders = new ManualResetEventSlim();
        var store = NewStore(queryReaderOpened: () =>
        {
            if (Interlocked.Increment(ref openedCount) == 2)
            {
                bothOpened.TrySetResult(true);
            }

            releaseReaders.Wait();
        });
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await store.WriteAsync([Sample(firstId, now), Sample(secondId, now)]);

        var firstQuery = Task.Run(() => store.QueryAsync(firstId, now - TimeSpan.FromMinutes(1), now + TimeSpan.FromMinutes(1)));
        var secondQuery = Task.Run(() => store.QueryAsync(secondId, now - TimeSpan.FromMinutes(1), now + TimeSpan.FromMinutes(1)));
        await bothOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var reset = store.ResetAsync();
        Assert.False(reset.IsCompleted);
        releaseReaders.Set();

        Assert.Single(await firstQuery);
        Assert.Single(await secondQuery);
        Assert.True(await reset.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(store.IsAvailable);
    }

    [Fact]
    public async Task FutureSchemaVersion_DisablesHistory_WithoutDeletingData()
    {
        var first = NewStore();
        await first.InitializeAsync();
        await first.WriteAsync([Sample(Guid.NewGuid(), DateTimeOffset.UtcNow)]);
        first.Dispose();
        SqliteConnection.ClearAllPools();

        // Simulate a database written by a newer app version.
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 999;";
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var second = NewStore();
        await second.InitializeAsync();

        Assert.False(second.IsAvailable);   // fail-safe: history disabled
        Assert.True(File.Exists(_dbPath));  // data preserved, never auto-deleted
    }

    [Fact]
    public async Task CorruptDatabase_InitializeDegradesGracefully_FileNotDeleted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await File.WriteAllTextAsync(_dbPath, "this is not a sqlite database");

        var store = NewStore();
        await store.InitializeAsync(); // must not throw

        Assert.False(store.IsAvailable);
        Assert.True(File.Exists(_dbPath)); // no destructive auto-delete
        // Queries/writes are soft no-ops while unavailable.
        Assert.Empty(await store.QueryAsync(Guid.NewGuid(), DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    [Fact]
    public async Task Reset_RecoversFromCorruption()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await File.WriteAllTextAsync(_dbPath, "corrupt");
        var store = NewStore();
        await store.InitializeAsync();
        Assert.False(store.IsAvailable);

        Assert.True(await store.ResetAsync());

        Assert.True(store.IsAvailable);
        var serverId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await store.WriteAsync([Sample(serverId, at)]);
        Assert.Single(await store.QueryAsync(serverId, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1)));
    }

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            var directory = Path.GetDirectoryName(_dbPath)!;
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                if (File.Exists(_dbPath + suffix))
                {
                    File.Delete(_dbPath + suffix);
                }
            }

            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
