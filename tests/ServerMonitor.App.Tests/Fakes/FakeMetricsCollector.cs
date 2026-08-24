using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.Fakes;

/// <summary>
/// Controllable <see cref="IServerMetricsCollector"/>. Backs the real
/// <c>ServerMetricsStore</c> under test so no SSH, network, Ubuntu, or
/// Credential Manager is involved. Can be gated to hold a collection open
/// (for single-flight tests) and mirrors the real collector's contract of
/// returning a typed <c>Cancelled</c> result rather than throwing.
/// </summary>
internal sealed class FakeMetricsCollector : IServerMetricsCollector
{
    private int _callCount;
    private TaskCompletionSource<ServerMetricsCollectionResult>? _gate;
    private Func<Server, ServerMetricsCollectionResult>? _resultFactory;
    private ServerMetricsCollectionResult? _result;

    public int CallCount => Volatile.Read(ref _callCount);

    public CancellationToken LastCancellationToken { get; private set; }

    // The store's Task.Yield() hops collection onto a thread-pool thread, so a
    // Result configured on the test thread is read on another thread. Volatile
    // access publishes the latest write across that boundary.
    public Func<Server, ServerMetricsCollectionResult>? ResultFactory
    {
        get => Volatile.Read(ref _resultFactory);
        set => Volatile.Write(ref _resultFactory, value);
    }

    public ServerMetricsCollectionResult? Result
    {
        get => Volatile.Read(ref _result);
        set => Volatile.Write(ref _result, value);
    }

    public TaskCompletionSource<ServerMetricsCollectionResult> Gate()
    {
        _gate = new TaskCompletionSource<ServerMetricsCollectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _gate;
    }

    public Task<ServerMetricsCollectionResult> CollectAsync(
        Server server,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        LastCancellationToken = cancellationToken;

        if (_gate is not null)
        {
            return _gate.Task;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(
                ServerMetricsCollectionResult.Failure(MetricsCollectionErrorCode.Cancelled));
        }

        var result = ResultFactory?.Invoke(server)
            ?? Result
            ?? throw new InvalidOperationException("No result configured on FakeMetricsCollector.");
        return Task.FromResult(result);
    }
}
