using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.Services;

public sealed class CompositeMonitoringCycleObserverTests
{
    private static MonitoringCycleCompletion Completion() => new()
    {
        ServerId = Guid.NewGuid(),
        CapturedAtUtc = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero),
        Outcome = MonitoringOutcome.Success,
        Health = ServerHealth.Healthy,
        Snapshot = null
    };

    private sealed class RecordingObserver : IMonitoringCycleObserver
    {
        public int Calls { get; private set; }

        public void OnCycleCompleted(MonitoringCycleCompletion completion) => Calls++;
    }

    private sealed class ThrowingObserver : IMonitoringCycleObserver
    {
        public int Calls { get; private set; }

        public void OnCycleCompleted(MonitoringCycleCompletion completion)
        {
            Calls++;
            throw new InvalidOperationException("boom");
        }
    }

    [Fact]
    public void AllObservers_ReceiveTheCycle()
    {
        var a = new RecordingObserver();
        var b = new RecordingObserver();
        var composite = new CompositeMonitoringCycleObserver(
            [a, b], NullLogger<CompositeMonitoringCycleObserver>.Instance);

        composite.OnCycleCompleted(Completion());

        Assert.Equal(1, a.Calls);
        Assert.Equal(1, b.Calls);
    }

    [Fact]
    public void FaultyObserver_IsIsolated_OthersStillRun()
    {
        // A throwing observer (history) must not stop a later observer (workloads) — and must never
        // propagate to the engine (§38).
        var throwing = new ThrowingObserver();
        var recording = new RecordingObserver();
        var composite = new CompositeMonitoringCycleObserver(
            [throwing, recording], NullLogger<CompositeMonitoringCycleObserver>.Instance);

        var exception = Record.Exception(() => composite.OnCycleCompleted(Completion()));

        Assert.Null(exception);          // never throws back to the engine
        Assert.Equal(1, throwing.Calls);
        Assert.Equal(1, recording.Calls); // the second observer still ran
    }

    [Fact]
    public void NoObservers_IsANoOp()
    {
        var composite = new CompositeMonitoringCycleObserver(
            [], NullLogger<CompositeMonitoringCycleObserver>.Instance);

        Assert.Null(Record.Exception(() => composite.OnCycleCompleted(Completion())));
    }
}
