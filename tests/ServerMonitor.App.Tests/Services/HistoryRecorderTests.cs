using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.Services;

public sealed class HistoryRecorderTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

    private static MonitoringCycleCompletion Completion(
        Guid serverId,
        DateTimeOffset at,
        MonitoringOutcome outcome,
        ServerHealth health,
        ServerMetricsSnapshot? snapshot) => new()
    {
        ServerId = serverId,
        CapturedAtUtc = at,
        Outcome = outcome,
        Health = health,
        Snapshot = snapshot
    };

    private static (HistoryRecorder recorder, HistorySampleChannel channel) NewRecorder(int capacity = 64)
    {
        var channel = new HistorySampleChannel(capacity);
        var recorder = new HistoryRecorder(channel, NullLogger<HistoryRecorder>.Instance);
        return (recorder, channel);
    }

    private static List<ServerHistorySample> Drain(HistorySampleChannel channel)
    {
        var list = new List<ServerHistorySample>();
        while (channel.Reader.TryRead(out var item))
        {
            if (item is HistorySampleItem sample)
            {
                list.Add(sample.Sample);
            }
        }

        return list;
    }

    [Fact]
    public void Success_RecordsRealMetricsAndHealth()
    {
        var (recorder, channel) = NewRecorder();
        var id = Guid.NewGuid();
        var snapshot = TestData.Snapshot(id, cpu: 20, memoryPercent: 40, diskPercent: 60);

        recorder.OnCycleCompleted(Completion(id, Base, MonitoringOutcome.Success, ServerHealth.Healthy, snapshot));

        var sample = Assert.Single(Drain(channel));
        Assert.Equal(20, sample.CpuPercent);
        Assert.Equal(40, sample.MemoryPercent);
        Assert.Equal(60, sample.DiskPercent);
        Assert.Equal(ServerHealth.Healthy, sample.Health);
        Assert.Equal(Base, sample.CapturedAtUtc);
    }

    [Fact]
    public void FreshFailureAfterSuccess_RecordsNullMetrics_NotStaleValue()
    {
        // The stale-vs-fresh invariant (spec §74): a success of CPU 20, then a failing cycle whose
        // fresh snapshot is null, must record null — never the recycled stale 20.
        var (recorder, channel) = NewRecorder();
        var id = Guid.NewGuid();
        recorder.OnCycleCompleted(Completion(id, Base, MonitoringOutcome.Success, ServerHealth.Healthy,
            TestData.Snapshot(id, cpu: 20)));
        recorder.OnCycleCompleted(Completion(id, Base + TimeSpan.FromSeconds(30), MonitoringOutcome.Retryable,
            ServerHealth.Offline, snapshot: null));

        var samples = Drain(channel);
        Assert.Equal(2, samples.Count);
        Assert.Equal(20, samples[0].CpuPercent);
        Assert.Null(samples[1].CpuPercent);
        Assert.Equal(ServerHealth.Offline, samples[1].Health);
    }

    [Fact]
    public void CancelledCycle_IsNotRecorded()
    {
        var (recorder, channel) = NewRecorder();
        var id = Guid.NewGuid();

        recorder.OnCycleCompleted(Completion(id, Base, MonitoringOutcome.Cancelled, ServerHealth.Unknown, snapshot: null));

        Assert.Empty(Drain(channel));
    }

    [Fact]
    public void WithinSamplingInterval_SameServer_OnlyFirstRecorded()
    {
        var (recorder, channel) = NewRecorder();
        var id = Guid.NewGuid();

        recorder.OnCycleCompleted(Completion(id, Base, MonitoringOutcome.Success, ServerHealth.Healthy, TestData.Snapshot(id, cpu: 10)));
        recorder.OnCycleCompleted(Completion(id, Base + TimeSpan.FromSeconds(10), MonitoringOutcome.Success, ServerHealth.Healthy, TestData.Snapshot(id, cpu: 11)));
        recorder.OnCycleCompleted(Completion(id, Base + TimeSpan.FromSeconds(20), MonitoringOutcome.Success, ServerHealth.Healthy, TestData.Snapshot(id, cpu: 12)));
        recorder.OnCycleCompleted(Completion(id, Base + TimeSpan.FromSeconds(30), MonitoringOutcome.Success, ServerHealth.Healthy, TestData.Snapshot(id, cpu: 13)));

        var samples = Drain(channel);
        Assert.Equal(2, samples.Count); // at 0s and 30s only
        Assert.Equal(10, samples[0].CpuPercent);
        Assert.Equal(13, samples[1].CpuPercent);
    }

    [Fact]
    public void DifferentServers_HaveIndependentCadence()
    {
        var (recorder, channel) = NewRecorder();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        recorder.OnCycleCompleted(Completion(a, Base, MonitoringOutcome.Success, ServerHealth.Healthy, TestData.Snapshot(a, cpu: 1)));
        recorder.OnCycleCompleted(Completion(b, Base, MonitoringOutcome.Success, ServerHealth.Healthy, TestData.Snapshot(b, cpu: 2)));

        Assert.Equal(2, Drain(channel).Count);
    }

    [Fact]
    public void NanMetric_SanitizedToNull()
    {
        var (recorder, channel) = NewRecorder();
        var id = Guid.NewGuid();

        recorder.OnCycleCompleted(Completion(id, Base, MonitoringOutcome.Success, ServerHealth.Healthy,
            TestData.Snapshot(id, cpu: double.NaN, memoryPercent: 200)));

        var sample = Assert.Single(Drain(channel));
        Assert.Null(sample.CpuPercent);    // NaN → null
        Assert.Null(sample.MemoryPercent); // absurd 200 → null
    }

    [Fact]
    public void FullQueue_DropsNewSamples_WithoutThrowingOrBlocking()
    {
        var (recorder, channel) = NewRecorder(capacity: 2);
        // Five distinct servers, all first samples pass the sampling policy; the bounded queue of 2
        // admits two and drops the rest — observably, never blocking the caller.
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            recorder.OnCycleCompleted(Completion(id, Base, MonitoringOutcome.Success, ServerHealth.Healthy, TestData.Snapshot(id, cpu: i)));
        }

        Assert.Equal(2, Drain(channel).Count);
    }
}
