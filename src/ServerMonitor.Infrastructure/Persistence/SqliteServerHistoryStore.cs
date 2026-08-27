using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;

namespace ServerMonitor.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IServerHistoryStore"/>. A thin, explicit persistence layer (no ORM):
/// versioned schema via <c>PRAGMA user_version</c>, WAL for concurrent reads, parameterized SQL only,
/// metrics-only rows. Degradable by design (ADR-015 §1/§9): a corrupt or unsupported database leaves
/// <see cref="IsAvailable"/> false and the app keeps monitoring; a transient lock is absorbed by
/// <c>busy_timeout</c>, never a retry storm. Writes/retention/clear/reset serialize through a single
/// write gate (§27); reads use their own pooled connection so WAL lets them run without blocking the
/// writer (§54).
/// </summary>
public sealed class SqliteServerHistoryStore : IServerHistoryStore, IDisposable
{
    private const int CurrentSchemaVersion = 2;

    private readonly HistoryStorageOptions _options;
    private readonly ILogger<SqliteServerHistoryStore> _logger;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly AsyncQueryResetGate _queryResetGate = new();

    private volatile bool _available;
    private volatile bool _canRetryInitialization;
    private bool _disposed;

    public SqliteServerHistoryStore(HistoryStorageOptions options, ILogger<SqliteServerHistoryStore> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (_options.MaxQueryRows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxQueryRows must be positive.");
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
    }

    public bool IsAvailable => _available;

    public bool CanRetryInitialization => _canRetryInitialization;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureDirectory();
            await using var connection = await OpenConfiguredAsync(cancellationToken).ConfigureAwait(false);
            await MigrateAsync(connection, cancellationToken).ConfigureAwait(false);
            _canRetryInitialization = false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _available = false;
            _canRetryInitialization = IsTransient(exception);
            _logger.LogError(
                "History database unavailable at startup. Monitoring continues. Reason: {Reason}.",
                Describe(exception));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task WriteAsync(IReadOnlyList<ServerHistorySample> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!_available || batch.Count == 0)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConfiguredAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT OR IGNORE INTO history_samples " +
                "(server_id, captured_at_utc, health, cpu_percent, memory_percent, disk_percent) " +
                "VALUES ($server_id, $captured_at_utc, $health, $cpu, $memory, $disk);";

            var serverId = command.Parameters.Add("$server_id", SqliteType.Text);
            var capturedAt = command.Parameters.Add("$captured_at_utc", SqliteType.Integer);
            var health = command.Parameters.Add("$health", SqliteType.Integer);
            var cpu = command.Parameters.Add("$cpu", SqliteType.Real);
            var memory = command.Parameters.Add("$memory", SqliteType.Real);
            var disk = command.Parameters.Add("$disk", SqliteType.Real);

            foreach (var sample in batch)
            {
                if (!HistorySampleValidator.IsValidTimestamp(sample.CapturedAtUtc))
                {
                    continue;
                }

                serverId.Value = sample.ServerId.ToString();
                capturedAt.Value = sample.CapturedAtUtc.ToUnixTimeMilliseconds();
                health.Value = Enum.IsDefined(sample.Health) ? (int)sample.Health : (int)ServerHealth.Unknown;
                cpu.Value = (object?)HistorySampleValidator.SanitizePercent(sample.CpuPercent) ?? DBNull.Value;
                memory.Value = (object?)HistorySampleValidator.SanitizePercent(sample.MemoryPercent) ?? DBNull.Value;
                disk.Value = (object?)HistorySampleValidator.SanitizePercent(sample.DiskPercent) ?? DBNull.Value;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DegradeIfFatal(exception, "write");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<ServerHistorySample>> QueryAsync(
        Guid serverId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default)
    {
        if (!_available)
        {
            return Array.Empty<ServerHistorySample>();
        }

        using var queryLease = await _queryResetGate.EnterQueryAsync(cancellationToken).ConfigureAwait(false);
        if (!_available)
        {
            return Array.Empty<ServerHistorySample>();
        }

        try
        {
                await using var connection = await OpenConfiguredAsync(cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT captured_at_utc, health, cpu_percent, memory_percent, disk_percent " +
                    "FROM history_samples " +
                    "WHERE server_id = $server_id AND captured_at_utc >= $start AND captured_at_utc <= $end " +
                    "ORDER BY captured_at_utc ASC " +
                    "LIMIT $limit;";
                command.Parameters.AddWithValue("$server_id", serverId.ToString());
                command.Parameters.AddWithValue("$start", startUtc.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$end", endUtc.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$limit", _options.MaxQueryRows + 1L);

                var results = new List<ServerHistorySample>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                _options.QueryReaderOpenedForTesting?.Invoke();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (results.Count == _options.MaxQueryRows)
                    {
                        throw new HistoryQueryLimitExceededException(_options.MaxQueryRows);
                    }

                    if (!TryReadTimestamp(reader, 0, out var capturedAtUtc))
                    {
                        continue;
                    }

                    results.Add(new ServerHistorySample
                    {
                        ServerId = serverId,
                        CapturedAtUtc = capturedAtUtc,
                        Health = ReadHealth(reader, 1),
                        CpuPercent = ReadPercent(reader, 2),
                        MemoryPercent = ReadPercent(reader, 3),
                        DiskPercent = ReadPercent(reader, 4)
                    });
                }

                return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HistoryQueryLimitExceededException)
        {
            throw;
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DegradeIfFatal(exception, "query");
            return Array.Empty<ServerHistorySample>();
        }
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
    {
        if (!_available)
        {
            return 0;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConfiguredAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM history_samples WHERE captured_at_utc < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoffUtc.ToUnixTimeMilliseconds());
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DegradeIfFatal(exception, "retention");
            return 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> ClearAsync(CancellationToken cancellationToken = default)
    {
        if (!_available)
        {
            return false;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConfiguredAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM history_samples;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            DegradeIfFatal(exception, "clear");
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> ResetAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _queryResetGate.EnterResetAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _available = false;
                _canRetryInitialization = false;
                // No query can be active while this gate is held. Release idle pooled handles to the
                // explicitly-reset file so Windows permits deletion.
                SqliteConnection.ClearAllPools();
                DeleteDatabaseFiles();
                EnsureDirectory();
                await using var connection = await OpenConfiguredAsync(cancellationToken).ConfigureAwait(false);
                await MigrateAsync(connection, cancellationToken).ConfigureAwait(false);
                return _available;
            }
            finally
            {
                _queryResetGate.ExitReset();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _available = false;
            _canRetryInitialization = IsTransient(exception);
            _logger.LogError("History database reset failed. Reason: {Reason}.", Describe(exception));
            return false;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConfiguredAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var busyTimeout = connection.CreateCommand())
            {
                // busy_timeout must be installed before journal_mode touches the locked database.
                busyTimeout.CommandText = "PRAGMA busy_timeout=5000;";
                await busyTimeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var pragma = connection.CreateCommand();
            // WAL is persisted in the file header (set once); synchronous/busy_timeout/foreign_keys
            // are per-connection and set on every open (ADR-015 §5).
            pragma.CommandText =
                "PRAGMA journal_mode=WAL;" +
                "PRAGMA synchronous=NORMAL;" +
                "PRAGMA foreign_keys=OFF;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            // A corrupt/locked database can throw after OpenAsync succeeds (e.g. reading the header
            // for a pragma). Dispose so the handle returns to the pool and never leaks a file lock —
            // otherwise a later reset/delete would be blocked.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var version = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (version > CurrentSchemaVersion)
        {
            // A newer app wrote this database. Never delete or downgrade it (spec §31): degrade.
            _available = false;
            _logger.LogWarning(
                "History database schema version {Version} is newer than supported {Supported}. History disabled; data preserved.",
                version,
                CurrentSchemaVersion);
            return;
        }

        if (version == 0)
        {
            await CreateCurrentSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            version = CurrentSchemaVersion;
        }

        if (version == 1)
        {
            await MigrateVersion1To2Async(connection, cancellationToken).ConfigureAwait(false);
            version = 2;
        }

        if (version != CurrentSchemaVersion ||
            !await HasExpectedSchemaAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("History database schema is logically incompatible.");
        }

        _available = true;
    }

    private static async Task CreateCurrentSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = CreateTableSql("history_samples") +
                             $"PRAGMA user_version={CurrentSchemaVersion};";
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuilds the v1 table to add constraints SQLite cannot add with ALTER COLUMN. Valid rows are
    /// preserved; malformed legacy percentages become NULL (unknown), never zero. The transaction
    /// makes the migration all-or-nothing and a logically incompatible v1 file is preserved.
    /// </summary>
    private static async Task MigrateVersion1To2Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            CreateTableSql("history_samples_v2") +
            "INSERT OR IGNORE INTO history_samples_v2 " +
            "(server_id, captured_at_utc, health, cpu_percent, memory_percent, disk_percent) " +
            "SELECT server_id, captured_at_utc, " +
            "CASE WHEN typeof(health)='integer' AND health BETWEEN 0 AND 4 THEN health ELSE 0 END, " +
            SanitizeLegacyMetricSql("cpu_percent") + ", " +
            SanitizeLegacyMetricSql("memory_percent") + ", " +
            SanitizeLegacyMetricSql("disk_percent") + " " +
            "FROM history_samples " +
            "WHERE typeof(server_id)='text' AND typeof(captured_at_utc)='integer';" +
            "DROP TABLE history_samples;" +
            "ALTER TABLE history_samples_v2 RENAME TO history_samples;" +
            "PRAGMA user_version=2;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string CreateTableSql(string tableName) =>
        $"CREATE TABLE {tableName} (" +
        "  server_id       TEXT    NOT NULL," +
        "  captured_at_utc INTEGER NOT NULL CHECK(typeof(captured_at_utc)='integer')," +
        "  health          INTEGER NOT NULL CHECK(typeof(health)='integer' AND health BETWEEN 0 AND 4)," +
        "  cpu_percent     REAL    NULL CHECK(cpu_percent IS NULL OR (typeof(cpu_percent) IN ('real','integer') AND cpu_percent BETWEEN 0 AND 100))," +
        "  memory_percent  REAL    NULL CHECK(memory_percent IS NULL OR (typeof(memory_percent) IN ('real','integer') AND memory_percent BETWEEN 0 AND 100))," +
        "  disk_percent    REAL    NULL CHECK(disk_percent IS NULL OR (typeof(disk_percent) IN ('real','integer') AND disk_percent BETWEEN 0 AND 100))," +
        "  PRIMARY KEY (server_id, captured_at_utc)" +
        ");";

    private static string SanitizeLegacyMetricSql(string columnName) =>
        $"CASE WHEN {columnName} IS NULL THEN NULL " +
        $"WHEN typeof({columnName}) IN ('real','integer') AND {columnName} BETWEEN 0 AND 100 " +
        $"THEN {columnName} ELSE NULL END";

    private static async Task<bool> HasExpectedSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var expected = new (string Name, string Type, bool NotNull, int PrimaryKeyOrder)[]
        {
            ("server_id", "TEXT", true, 1),
            ("captured_at_utc", "INTEGER", true, 2),
            ("health", "INTEGER", true, 0),
            ("cpu_percent", "REAL", false, 0),
            ("memory_percent", "REAL", false, 0),
            ("disk_percent", "REAL", false, 0)
        };

        var actual = new List<(string Name, string Type, bool NotNull, int PrimaryKeyOrder)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(history_samples);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                actual.Add((reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, checked((int)reader.GetInt64(5))));
            }
        }

        if (!actual.SequenceEqual(expected))
        {
            return false;
        }

        await using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='history_samples';";
        var sql = await schema.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return sql is not null &&
               sql.Contains("CHECK", StringComparison.OrdinalIgnoreCase) &&
               sql.Contains("cpu_percent BETWEEN 0 AND 100", StringComparison.OrdinalIgnoreCase) &&
               sql.Contains("memory_percent BETWEEN 0 AND 100", StringComparison.OrdinalIgnoreCase) &&
               sql.Contains("disk_percent BETWEEN 0 AND 100", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<long> ReadUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is long value ? value : Convert.ToInt64(scalar);
    }

    private static bool TryReadTimestamp(SqliteDataReader reader, int ordinal, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (reader.IsDBNull(ordinal) || reader.GetValue(ordinal) is not long milliseconds)
        {
            return false;
        }

        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            return HistorySampleValidator.IsValidTimestamp(timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static ServerHealth ReadHealth(SqliteDataReader reader, int ordinal)
    {
        if (!reader.IsDBNull(ordinal) && reader.GetValue(ordinal) is long value)
        {
            var health = (ServerHealth)value;
            if (Enum.IsDefined(health))
            {
                return health;
            }
        }

        return ServerHealth.Unknown;
    }

    private static double? ReadPercent(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal) switch
        {
            double real => real,
            long integer => integer,
            _ => (double?)null
        };
        return HistorySampleValidator.SanitizePercent(value);
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(_options.DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private void DeleteDatabaseFiles()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var path = _options.DatabasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Marks the store unavailable only for fatal (corruption/not-a-database) errors, so the UI can
    /// offer an explicit reset. Transient conditions (busy/locked) are left alone — <c>busy_timeout</c>
    /// already absorbs them and availability must not flap.
    /// </summary>
    private void DegradeIfFatal(Exception exception, string operation)
    {
        if (exception is SqliteException sqlite &&
            sqlite.SqliteErrorCode is SqliteCorruptCode or SqliteNotADatabaseCode)
        {
            _available = false;
            _logger.LogError(
                "History database is corrupt (during {Operation}); history disabled until reset. Reason: {Reason}.",
                operation,
                Describe(exception));
            return;
        }

        _logger.LogWarning("History {Operation} failed transiently. Reason: {Reason}.", operation, Describe(exception));
    }

    private const int SqliteCorruptCode = 11;    // SQLITE_CORRUPT
    private const int SqliteNotADatabaseCode = 26; // SQLITE_NOTADB
    private const int SqliteBusyCode = 5;          // SQLITE_BUSY
    private const int SqliteLockedCode = 6;        // SQLITE_LOCKED

    private static bool IsTransient(Exception exception) =>
        exception is SqliteException sqlite &&
        sqlite.SqliteErrorCode is SqliteBusyCode or SqliteLockedCode;

    /// <summary>Sanitized, non-sensitive description of a failure (never raw SSH/credential text).</summary>
    private static string Describe(Exception exception) =>
        exception is SqliteException sqlite
            ? $"SqliteError {sqlite.SqliteErrorCode}"
            : exception.GetType().Name;

    /// <summary>Async reader/writer gate: queries share access; explicit reset waits for all readers
    /// and blocks new ones without serializing unrelated range queries.</summary>
    private sealed class AsyncQueryResetGate
    {
        private readonly object _sync = new();
        private int _activeQueries;
        private bool _resetPending;
        private TaskCompletionSource<bool>? _queriesAllowed;
        private TaskCompletionSource<bool>? _queriesDrained;

        public async Task<IDisposable> EnterQueryAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (!_resetPending)
                    {
                        _activeQueries++;
                        return new QueryLease(this);
                    }

                    wait = _queriesAllowed!.Task;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task EnterResetAsync(CancellationToken cancellationToken)
        {
            Task? wait = null;
            lock (_sync)
            {
                if (_resetPending)
                {
                    throw new InvalidOperationException("A history reset is already pending.");
                }

                _resetPending = true;
                _queriesAllowed = NewCompletion();
                if (_activeQueries > 0)
                {
                    _queriesDrained = NewCompletion();
                    wait = _queriesDrained.Task;
                }
            }

            if (wait is null)
            {
                return;
            }

            try
            {
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ExitReset();
                throw;
            }
        }

        public void ExitReset()
        {
            TaskCompletionSource<bool>? allowed;
            lock (_sync)
            {
                if (!_resetPending)
                {
                    return;
                }

                _resetPending = false;
                _queriesDrained = null;
                allowed = _queriesAllowed;
                _queriesAllowed = null;
            }

            allowed?.TrySetResult(true);
        }

        private void ExitQuery()
        {
            TaskCompletionSource<bool>? drained = null;
            lock (_sync)
            {
                _activeQueries--;
                if (_activeQueries == 0 && _resetPending)
                {
                    drained = _queriesDrained;
                }
            }

            drained?.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> NewCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class QueryLease(AsyncQueryResetGate owner) : IDisposable
        {
            private AsyncQueryResetGate? _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitQuery();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Do not dispose the semaphores here: bounded host shutdown may return while a provider call
        // that ignores cancellation is still unwinding. SemaphoreSlim owns no native handle unless
        // AvailableWaitHandle is requested (it is not), and leaving it alive prevents release-after-
        // dispose races while the connection/command scopes close their real handles.
    }
}
