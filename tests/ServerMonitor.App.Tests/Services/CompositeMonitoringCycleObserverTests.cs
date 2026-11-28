using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;
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

    private sealed class OrderObserver(string name, List<string> calls, bool throws = false) : IMonitoringCycleObserver
    {
        public void OnCycleCompleted(MonitoringCycleCompletion completion)
        {
            calls.Add(name);
            if (throws)
            {
                throw new InvalidOperationException("boom");
            }
        }
    }

    private static CompositeMonitoringCycleObserver Create(
        IEnumerable<IMonitoringCycleObserver> builtIn, IEnumerable<MonitoringCycleObserverRegistration> additional) =>
        CompositeMonitoringCycleObserver.Create(builtIn, additional, NullLogger<CompositeMonitoringCycleObserver>.Instance);

    [Fact]
    public void Additional_observers_run_once_each_after_the_built_in_ones_in_registration_order()
    {
        var calls = new List<string>();
        var composite = Create(
            [new OrderObserver("history", calls), new OrderObserver("workloads", calls), new OrderObserver("widget", calls)],
            [new(new OrderObserver("extra1", calls)), new(new OrderObserver("extra2", calls))]);

        composite.OnCycleCompleted(Completion());

        Assert.Equal(["history", "workloads", "widget", "extra1", "extra2"], calls);
    }

    [Fact]
    public void A_throwing_observer_never_stops_the_others_or_reaches_the_engine()
    {
        var calls = new List<string>();
        var composite = Create(
            [new OrderObserver("history", calls, throws: true), new OrderObserver("widget", calls)],
            [new(new OrderObserver("extra1", calls, throws: true)), new(new OrderObserver("extra2", calls))]);

        var exception = Record.Exception(() => composite.OnCycleCompleted(Completion()));

        Assert.Null(exception);
        Assert.Equal(["history", "widget", "extra1", "extra2"], calls);
    }

    [Fact]
    public void With_no_registration_the_composite_is_exactly_the_built_in_list()
    {
        var calls = new List<string>();
        var composite = Create(
            [new OrderObserver("history", calls), new OrderObserver("workloads", calls), new OrderObserver("widget", calls)],
            []);

        composite.OnCycleCompleted(Completion());
        composite.OnCycleCompleted(Completion());

        Assert.Equal(["history", "workloads", "widget", "history", "workloads", "widget"], calls);
    }

    [Fact]
    public void Registrations_resolved_from_the_container_keep_their_order()
    {
        var calls = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(new MonitoringCycleObserverRegistration(new OrderObserver("extra1", calls)));
        services.AddSingleton(new MonitoringCycleObserverRegistration(new OrderObserver("extra2", calls)));
        using var provider = services.BuildServiceProvider();

        var composite = Create(
            [new OrderObserver("history", calls)],
            provider.GetServices<MonitoringCycleObserverRegistration>());
        composite.OnCycleCompleted(Completion());

        Assert.Equal(["history", "extra1", "extra2"], calls);
    }

    [Fact]
    public void A_registration_without_an_observer_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new MonitoringCycleObserverRegistration(null!));
    }

    [Fact]
    public void The_app_appends_the_registrations_and_never_registers_one_itself()
    {
        var app = AppSourceTree.CodeWithoutComments("App.xaml.cs");

        Assert.Contains(
            "services.AddSingleton<IMonitoringCycleObserver>(sp => CompositeMonitoringCycleObserver.Create(",
            app,
            StringComparison.Ordinal);
        Assert.Contains("sp.GetServices<MonitoringCycleObserverRegistration>(),", app, StringComparison.Ordinal);
        Assert.DoesNotContain("new CompositeMonitoringCycleObserver(", app, StringComparison.Ordinal);

        var constructing = AppSourceTree.Files(".cs")
            .Where(file => AppSourceTree.CodeWithoutComments(file).Contains(
                "new MonitoringCycleObserverRegistration(", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(constructing);
    }
}
