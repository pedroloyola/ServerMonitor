using ServerMonitor.Core.History;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>In-memory <see cref="IServerHistoryStore"/> for writer/query tests: records writes,
/// serves scripted query results, and counts retention/init calls. No SQLite involved.</summary>
internal sealed class FakeServerHistoryStore : IServerHistoryStore
{
    private readonly object _sync = new();
    private readonly List<ServerHistorySample> _written = [];
    private readonly Dictionary<int, TaskCompletionSource<bool>> _retentionWaiters = [];
    private readonly Dictionary<int, TaskCompletionSource<bool>> _writeWaiters = [];
    private readonly Dictionary<int, TaskCompletionSource<bool>> _initializeWaiters = [];
    private int _initializeCount;

    public bool Available { get; set; } = true;

    public bool IsAvailable => Available;

    public bool CanRetryInitialization { get; set; }

    public int InitializeCount { get { lock (_sync) { return _initializeCount; } } }

    public int? BecomeAvailableOnInitializeCount { get; set; }

    public TaskCompletionSource<bool>? WriteBlocker { get; set; }

    public TaskCompletionSource<bool> WriteEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _writeBatchCount;

    public int WriteBatchCount { get { lock (_sync) { return _writeBatchCount; } } }

    private int _retentionCallCount;

    public int RetentionCallCount { get { lock (_sync) { return _retentionCallCount; } } }

    private int _clearCallCount;

    public int ClearCallCount { get { lock (_sync) { return _clearCallCount; } } }

    private int _resetCallCount;

    public int ResetCallCount { get { lock (_sync) { return _resetCallCount; } } }

    public bool ClearSucceeds { get; set; } = true;

    public bool ResetSucceeds { get; set; } = true;

    private DateTimeOffset? _lastRetentionCutoff;

    public DateTimeOffset? LastRetentionCutoff { get { lock (_sync) { return _lastRetentionCutoff; } } }

    public IReadOnlyList<ServerHistorySample> Written
    {
        get { lock (_sync) { return _written.ToList(); } }
    }

    public Func<Guid, DateTimeOffset, DateTimeOffset, IReadOnlyList<ServerHistorySample>>? QueryFactory { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _initializeCount++;
            if (BecomeAvailableOnInitializeCount is { } target && _initializeCount >= target)
            {
                Available = true;
                CanRetryInitialization = false;
            }

            CompleteWaiters(_initializeWaiters, _initializeCount);
        }

        return Task.CompletedTask;
    }

    public async Task WriteAsync(IReadOnlyList<ServerHistorySample> batch, CancellationToken cancellationToken = default)
    {
        WriteEntered.TrySetResult(true);
        if (WriteBlocker is { } blocker)
        {
            await blocker.Task.ConfigureAwait(false); // deliberately non-cooperative for shutdown tests
        }

        lock (_sync)
        {
            _written.AddRange(batch);
            _writeBatchCount++;
            CompleteWaiters(_writeWaiters, _written.Count);
        }

    }

    public Task<IReadOnlyList<ServerHistorySample>> QueryAsync(
        Guid serverId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default)
    {
        var result = QueryFactory?.Invoke(serverId, startUtc, endUtc)
                     ?? (IReadOnlyList<ServerHistorySample>)Array.Empty<ServerHistorySample>();
        return Task.FromResult(result);
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _retentionCallCount++;
            _lastRetentionCutoff = cutoffUtc;
            CompleteWaiters(_retentionWaiters, _retentionCallCount);
        }
        return Task.FromResult(0);
    }

    public Task<bool> ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _clearCallCount++;
        }
        if (!ClearSucceeds)
        {
            return Task.FromResult(false);
        }

        lock (_sync)
        {
            _written.Clear();
        }

        return Task.FromResult(true);
    }

    public Task<bool> ResetAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _resetCallCount++;
            if (!ResetSucceeds)
            {
                return Task.FromResult(false);
            }

            _written.Clear();
        }

        Available = true;
        CanRetryInitialization = false;
        return Task.FromResult(true);
    }

    public Task WaitForRetentionCallsAsync(int count) => WaitForAsync(_retentionWaiters, count, () => _retentionCallCount);

    public Task WaitForWrittenCountAsync(int count) => WaitForAsync(_writeWaiters, count, () => _written.Count);

    public Task WaitForInitializeCallsAsync(int count) => WaitForAsync(_initializeWaiters, count, () => _initializeCount);

    private Task WaitForAsync(
        Dictionary<int, TaskCompletionSource<bool>> waiters,
        int count,
        Func<int> current)
    {
        lock (_sync)
        {
            if (current() >= count)
            {
                return Task.CompletedTask;
            }

            if (!waiters.TryGetValue(count, out var completion))
            {
                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters[count] = completion;
            }

            return completion.Task;
        }
    }

    private static void CompleteWaiters(Dictionary<int, TaskCompletionSource<bool>> waiters, int current)
    {
        foreach (var pair in waiters.Where(pair => pair.Key <= current).ToArray())
        {
            waiters.Remove(pair.Key);
            pair.Value.TrySetResult(true);
        }
    }
}
