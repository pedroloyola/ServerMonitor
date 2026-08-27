using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// The M6 → history seam: the engine publishes exactly one <see cref="MonitoringCycleCompletion"/>
/// per completed cycle, carrying the <b>fresh</b> snapshot (null on failure). This is what guarantees
/// history never records a recycled stale value.
/// </summary>
public sealed class MonitoringEngineHistoryObserverTests
{
    private static CancellationToken TestTimeout => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static MonitoringOptions ManualDrive() => new()
    {
        InitialDelay = TimeSpan.FromHours(1),
        StartupStagger = TimeSpan.Zero,
        AttentionInterval = TimeSpan.FromHours(1),
        RetryDelays = []
    };

    private sealed class RecordingObserver : IMonitoringCycleObserver
    {
        private readonly object _sync = new();
        private readonly List<MonitoringCycleCompletion> _completions = [];

        public IReadOnlyList<MonitoringCycleCompletion> Completions
        {
            get { lock (_sync) { return _completions.ToList(); } }
        }

        public void OnCycleCompleted(MonitoringCycleCompletion completion)
        {
            lock (_sync)
            {
                _completions.Add(completion);
            }
        }
    }

    private static async Task<(MonitoringEngine engine, ScriptedMetricsStore store, RecordingObserver observer)> StartAsync(Server server)
    {
        var service = new FakeServerService();
        service.Servers.Add(server);
        var store = new ScriptedMetricsStore();
        var state = new ServerMonitoringStateStore();
        var observer = new RecordingObserver();
        var engine = new MonitoringEngine(
            service, store, state, NullLogger<MonitoringEngine>.Instance,
            new FakeTimeProvider(), ManualDrive(), observer);
        await engine.StartMonitoringAsync();
        return (engine, store, observer);
    }

    [Fact]
    public async Task Success_PublishesCompletionWithFreshSnapshot()
    {
        var server = TestData.LinuxServer();
        var (engine, store, observer) = await StartAsync(server);
        await using (engine)
        {
            store.ResultFactory = (s, _) => TestData.Success(TestData.Snapshot(s.Id, cpu: 33));

            await engine.RefreshNowAsync(server.Id, TestTimeout);

            var completion = Assert.Single(observer.Completions);
            Assert.Equal(MonitoringOutcome.Success, completion.Outcome);
            Assert.Equal(ServerHealth.Healthy, completion.Health);
            Assert.NotNull(completion.Snapshot);
            Assert.Equal(33, completion.Snapshot!.CpuUsagePercent);
        }
    }

    [Fact]
    public async Task Failure_PublishesCompletionWithNullSnapshot()
    {
        var server = TestData.LinuxServer();
        var (engine, store, observer) = await StartAsync(server);
        await using (engine)
        {
            store.ResultFactory = (_, _) => TestData.Failure(MetricsCollectionErrorCode.ConnectionFailed);

            await engine.RefreshNowAsync(server.Id, TestTimeout);

            var completion = Assert.Single(observer.Completions);
            Assert.Equal(MonitoringOutcome.Retryable, completion.Outcome);
            Assert.Equal(ServerHealth.Offline, completion.Health);
            Assert.Null(completion.Snapshot); // fresh failure carries no snapshot
        }
    }
}
