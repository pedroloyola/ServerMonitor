using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure.Tests.Persistence;

public sealed class SqliteServerHistoryStoreTests : IDisposable
{
    /// <summary>
    /// Deadlock guard only: the concurrency tests below wait on signals they control (a reader opening, a gate
    /// releasing), never on elapsed time, so a working store never reaches this bound however slow the runner.
    /// </summary>
    private static readonly TimeSpan DeadlockGuard = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs a query on its own thread: the reader hooks below park the calling thread, and two parked thread-pool
    /// threads on a busy runner can delay the next query (or the test's own continuation) by seconds.
    /// </summary>
    private static Task<T> RunOnDedicatedThread<T>(Func<Task<T>> query) =>
        Task.Factory.StartNew(query, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

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
        Assert.Equal(3L, await version.ExecuteScalarAsync());
        Assert.Null(row.Load1);
    }

    /// <summary>The v2 table exactly as v2 created it (the DDL shipped before schema v3), with a v2 row in it.</summary>
    private async Task CreateVersion2DatabaseAsync(Guid serverId, DateTimeOffset at)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE history_samples (" +
            "  server_id       TEXT    NOT NULL," +
            "  captured_at_utc INTEGER NOT NULL CHECK(typeof(captured_at_utc)='integer')," +
            "  health          INTEGER NOT NULL CHECK(typeof(health)='integer' AND health BETWEEN 0 AND 4)," +
            "  cpu_percent     REAL    NULL CHECK(cpu_percent IS NULL OR (typeof(cpu_percent) IN ('real','integer') AND cpu_percent BETWEEN 0 AND 100))," +
            "  memory_percent  REAL    NULL CHECK(memory_percent IS NULL OR (typeof(memory_percent) IN ('real','integer') AND memory_percent BETWEEN 0 AND 100))," +
            "  disk_percent    REAL    NULL CHECK(disk_percent IS NULL OR (typeof(disk_percent) IN ('real','integer') AND disk_percent BETWEEN 0 AND 100))," +
            "  PRIMARY KEY (server_id, captured_at_utc)" +
            ");" +
            "PRAGMA user_version=2;" +
            "INSERT INTO history_samples VALUES ($id, $at, 1, 12.5, 34.5, 56.5);";
        command.Parameters.AddWithValue("$id", serverId.ToString());
        command.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Version2Migration_PreservesRows_AddsNullLoad1_AndBumpsToVersion3()
    {
        var serverId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        await CreateVersion2DatabaseAsync(serverId, at);

        var store = NewStore();
        await store.InitializeAsync();

        Assert.True(store.IsAvailable);
        var row = Assert.Single(await store.QueryAsync(serverId, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1)));
        Assert.Equal(ServerHealth.Healthy, row.Health);
        Assert.Equal(12.5, row.CpuPercent);
        Assert.Equal(34.5, row.MemoryPercent);
        Assert.Equal(56.5, row.DiskPercent);
        Assert.Null(row.Load1); // unknown for every pre-v3 row, never 0

        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await verify.OpenAsync();
        await using var version = verify.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(3L, await version.ExecuteScalarAsync());

        // The migrated file is writable with a load, and still rejects a negative one.
        var later = at + TimeSpan.FromMinutes(1);
        await store.WriteAsync([Sample(serverId, later) with { Load1 = 3.25 }]);
        var written = Assert.Single(await store.QueryAsync(serverId, later, later + TimeSpan.FromSeconds(1)));
        Assert.Equal(3.25, written.Load1);

        await using var negative = verify.CreateCommand();
        negative.CommandText = "INSERT INTO history_samples VALUES ($id, $at, 1, NULL, NULL, NULL, -0.5);";
        negative.Parameters.AddWithValue("$id", serverId.ToString());
        negative.Parameters.AddWithValue("$at", (later + TimeSpan.FromMinutes(1)).ToUnixTimeMilliseconds());
        var exception = await Assert.ThrowsAsync<SqliteException>(() => negative.ExecuteNonQueryAsync());
        Assert.Equal(19, exception.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    [Fact]
    public async Task Version2Migration_ReopenedFile_IsANoOp()
    {
        var serverId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        await CreateVersion2DatabaseAsync(serverId, at);

        var first = NewStore();
        await first.InitializeAsync();
        first.Dispose();
        SqliteConnection.ClearAllPools();

        var second = NewStore();
        await second.InitializeAsync();

        Assert.True(second.IsAvailable);
        Assert.Single(await second.QueryAsync(serverId, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Load1_RoundTrips_AboveOneHundred_AndNullStaysNull()
    {
        var store = NewStore();
        await store.InitializeAsync();
        var serverId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        await store.WriteAsync(
        [
            Sample(serverId, at) with { Load1 = 0 },
            Sample(serverId, at + TimeSpan.FromSeconds(30)) with { Load1 = 128.5 }, // not a percentage: no upper bound
            Sample(serverId, at + TimeSpan.FromSeconds(60)) with { Load1 = null },
            Sample(serverId, at + TimeSpan.FromSeconds(90)) with { Load1 = -1 } // sanitized to unknown on write
        ]);

        var rows = await store.QueryAsync(serverId, at, at + TimeSpan.FromMinutes(2));

        Assert.Equal([0, 128.5, null, null], rows.Select(r => r.Load1).ToArray());
    }

    [Fact]
    public async Task SchemaChecks_RejectNegativeLoad1DirectWrite()
    {
        var store = NewStore();
        await store.InitializeAsync();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO history_samples VALUES ($id, $at, 1, NULL, NULL, NULL, -1);";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var exception = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(19, exception.SqliteErrorCode); // SQLITE_CONSTRAINT
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
                "INSERT INTO history_samples VALUES ($id, $at, 999, -10, 101, 'NaN', -1);";
            command.Parameters.AddWithValue("$id", serverId.ToString());
            command.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync();
        }

        var row = Assert.Single(await store.QueryAsync(serverId, at - TimeSpan.FromMinutes(1), at + TimeSpan.FromMinutes(1)));
        Assert.Equal(ServerHealth.Unknown, row.Health);
        Assert.Null(row.CpuPercent);
        Assert.Null(row.MemoryPercent);
        Assert.Null(row.DiskPercent);
        Assert.Null(row.Load1);
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
            "INSERT INTO history_samples VALUES ($id, $at, 1, 101, NULL, NULL, NULL);";
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
            command.CommandText = "CREATE TABLE wrong_table(value TEXT); PRAGMA user_version=3;";
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

    /// <summary>
    /// Formerly <c>QueryCancellation_AfterRealReaderOpened_CompletesWithinBound</c>. Its stopwatch started BEFORE the
    /// query was scheduled, so it timed thread-pool scheduling, the SQLite open and the read setup — not the
    /// cancellation — and failed on slow CI runners ("Cancellation took 00:00:04.14", run 37057158486). The guarantee
    /// is functional and is now asserted without the clock: with rows present and the real reader open, a token
    /// cancelled before the reader may continue makes the query surface the cancellation — never a partial or empty
    /// result — and the query gives the read gate back, so a reset (which waits for every active query) proceeds.
    /// It does NOT prove WHICH read observes the token (a store that only checked it after the scan would also pass;
    /// independent review r1, SHOULD-1), so the name claims only what is proven.
    /// </summary>
    [Fact]
    public async Task QueryCancellation_AfterRealReaderOpened_SurfacesCancellation_AndReleasesTheReadGate()
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
        await store.WriteAsync([Sample(serverId, now), Sample(serverId, now + TimeSpan.FromSeconds(10))]);

        using var cancellation = new CancellationTokenSource();
        // A dedicated thread: the reader hook parks this thread, and the query must not wait for the thread pool to
        // even start on a busy runner.
        var query = RunOnDedicatedThread(() => store.QueryAsync(
            serverId,
            now - TimeSpan.FromHours(1),
            now + TimeSpan.FromHours(1),
            cancellation.Token));

        await readerOpened.Task.WaitAsync(DeadlockGuard); // the real reader is open, parked before its first read
        cancellation.Cancel();
        releaseReader.Set();

        // Rows exist and the reader is open: anything but a cancellation means the token was not honoured.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.WaitAsync(DeadlockGuard));

        // The cancelled query left the read gate: a reset (which waits for all active queries) completes.
        Assert.True(await store.ResetAsync().WaitAsync(DeadlockGuard));
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

        // Both queries park inside their open readers (dedicated threads, so neither waits on the pool to start). The
        // reset then cannot complete until both leave the read gate: a deterministic "not yet", not a timing guess.
        var firstQuery = RunOnDedicatedThread(() => store.QueryAsync(firstId, now - TimeSpan.FromMinutes(1), now + TimeSpan.FromMinutes(1)));
        var secondQuery = RunOnDedicatedThread(() => store.QueryAsync(secondId, now - TimeSpan.FromMinutes(1), now + TimeSpan.FromMinutes(1)));
        Task<bool>? reset = null;
        var failed = false;
        try
        {
            await bothOpened.Task.WaitAsync(DeadlockGuard);

            reset = store.ResetAsync();
            Assert.False(reset.IsCompleted);
            releaseReaders.Set();

            Assert.Single(await firstQuery.WaitAsync(DeadlockGuard));
            Assert.Single(await secondQuery.WaitAsync(DeadlockGuard));
            Assert.True(await reset.WaitAsync(DeadlockGuard));
            Assert.True(store.IsAvailable);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            // UI.12 ATLAS-SQLITE-CLEANUP: whatever failed above, the parked readers are released and both queries (and a
            // started reset) are drained before the event is disposed - never left blocked on a dedicated thread. A
            // cleanup problem never masks the original failure; on a passing run it is reported.
            releaseReaders.Set();
            try
            {
                await Task.WhenAll(reset is null ? [firstQuery, secondQuery] : [firstQuery, secondQuery, reset]).WaitAsync(DeadlockGuard);
            }
            catch (Exception) when (failed)
            {
            }
        }
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
