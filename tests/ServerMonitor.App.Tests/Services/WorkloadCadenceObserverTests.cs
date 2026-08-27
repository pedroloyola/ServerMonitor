using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Monitoring;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Tests.Services;

public sealed class WorkloadCadenceObserverTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);

    private static MonitoringCycleCompletion Completion(Guid id, DateTimeOffset at, MonitoringOutcome outcome) => new()
    {
        ServerId = id,
        CapturedAtUtc = at,
        Outcome = outcome,
        Health = ServerHealth.Healthy,
        Snapshot = null
    };

    private static (WorkloadCadenceObserver observer, WorkloadRequestQueue queue) New(int capacity = 64)
    {
        var queue = new WorkloadRequestQueue(new WorkloadOptions { QueueCapacity = capacity });
        var observer = new WorkloadCadenceObserver(
            queue,
            NullLogger<WorkloadCadenceObserver>.Instance,
            new WorkloadCadencePolicy(TimeSpan.FromSeconds(60)));
        return (observer, queue);
    }

    private static int Drain(WorkloadRequestQueue queue)
    {
        var count = 0;
        while (queue.Reader.TryRead(out _))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void FirstCycle_Enqueues()
    {
        var (observer, queue) = New();
        var id = Guid.NewGuid();

        observer.OnCycleCompleted(Completion(id, Base, MonitoringOutcome.Success));

        Assert.Equal(1, Drain(queue));
    }

    [Fact]
    public void CancelledCycle_IsIgnored()
    {
        var (observer, queue) = New();
        var id = Guid.NewGuid();

        observer.OnCycleCompleted(Completion(id, Base, MonitoringOutcome.Cancelled));

        Assert.Equal(0, Drain(queue));
    }

    [Fact]
    public void WithinCadence_SameServer_OnlyFirstEnqueued()
    {
        var (observer, queue) = New();
        var id = Guid.NewGuid();

        // Host polling at 10s; the 60s cadence admits the first and then one at 60s.
        for (var i = 0; i < 7; i++)
        {
            observer.OnCycleCompleted(Completion(id, Base + TimeSpan.FromSeconds(i * 10), MonitoringOutcome.Success));
        }

        Assert.Equal(2, Drain(queue)); // at 0s and 60s
    }

    [Fact]
    public void ExactCadenceBoundary_IsDue_ButOneTickEarlyIsNot()
    {
        var (observer, queue) = New();
        var id = Guid.NewGuid();

        observer.OnCycleCompleted(Completion(id, Base, MonitoringOutcome.Success));
        observer.OnCycleCompleted(Completion(id, Base + TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1), MonitoringOutcome.Success));
        observer.OnCycleCompleted(Completion(id, Base + TimeSpan.FromSeconds(60), MonitoringOutcome.Success));

        Assert.Equal(2, Drain(queue));
    }

    [Fact]
    public void Forget_RemovesCadenceMarker_SoNextCycleIsDue()
    {
        var (observer, queue) = New();
        var id = Guid.NewGuid();

        observer.OnCycleCompleted(Completion(id, Base, MonitoringOutcome.Success));
        observer.Forget(id);
        observer.OnCycleCompleted(Completion(id, Base + TimeSpan.FromSeconds(1), MonitoringOutcome.Success));

        Assert.Equal(2, Drain(queue));
    }

    [Fact]
    public void DifferentServers_IndependentCadence()
    {
        var (observer, queue) = New();

        observer.OnCycleCompleted(Completion(Guid.NewGuid(), Base, MonitoringOutcome.Success));
        observer.OnCycleCompleted(Completion(Guid.NewGuid(), Base, MonitoringOutcome.Success));

        Assert.Equal(2, Drain(queue));
    }

    [Fact]
    public void FullQueue_DropsWithoutThrowing_AndAdvancesMarker()
    {
        var (observer, queue) = New(capacity: 2);

        // Five distinct servers all due on their first cycle; the bounded queue of 2 admits two and drops
        // the rest observably, never throwing.
        for (var i = 0; i < 5; i++)
        {
            observer.OnCycleCompleted(Completion(Guid.NewGuid(), Base, MonitoringOutcome.Success));
        }

        Assert.Equal(2, Drain(queue));
    }

    [Fact]
    public void DroppedRequest_DoesNotTightRetryBeforeCadenceIsDue()
    {
        var (observer, queue) = New(capacity: 1);
        var filler = Guid.NewGuid();
        var dropped = Guid.NewGuid();

        observer.OnCycleCompleted(Completion(filler, Base, MonitoringOutcome.Success));
        observer.OnCycleCompleted(Completion(dropped, Base, MonitoringOutcome.Success));
        Assert.Equal(1, Drain(queue));

        observer.OnCycleCompleted(Completion(dropped, Base + TimeSpan.FromSeconds(1), MonitoringOutcome.Success));
        Assert.Equal(0, Drain(queue));

        observer.OnCycleCompleted(Completion(dropped, Base + TimeSpan.FromSeconds(60), MonitoringOutcome.Success));
        Assert.Equal(1, Drain(queue));
    }
}
