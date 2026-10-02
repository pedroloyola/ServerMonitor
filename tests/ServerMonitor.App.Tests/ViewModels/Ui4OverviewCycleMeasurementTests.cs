using System.Diagnostics;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using Xunit.Abstractions;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.4 Cortex r1 SHOULD-2 measurement: one full engine cycle over 500 servers (every server publishes a new state once)
/// against the dashboard + an open Servidores directory. Reports wall time and the overview notifications raised; the
/// assertion pins the notification budget so the coalescing cannot silently regress.
/// </summary>
public sealed class Ui4OverviewCycleMeasurementTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A500ServerCycle_StaysWithinTheNotificationBudget()
    {
        var fleet = new Ui4TestKit.Fleet();
        for (var i = 1; i <= 500; i++)
        {
            fleet.Add($"node-{i:000}", i % 17 == 0 ? ServerHealth.Warning : ServerHealth.Healthy, 10 + (i % 50), 20, i % 17 == 0 ? 85 : 30);
        }

        var kit = Ui4TestKit.Create(fleet);
        await kit.Dashboard.LoadAsync();
        using var directory = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization);
        // The UI dispatcher's role in production: queue the coalesced recompute; run it once after the burst.
        var queued = new List<Action>();
        kit.Dashboard.OverviewScheduler = action =>
        {
            queued.Add(action);
            return true;
        };
        var dashboardNotifications = 0;
        var directoryNotifications = 0;
        kit.Dashboard.PropertyChanged += (_, _) => dashboardNotifications++;
        directory.PropertyChanged += (_, _) => directoryNotifications++;

        var stopwatch = Stopwatch.StartNew();
        foreach (var entry in fleet.Entries)
        {
            // The engine re-publishes the same health (the common case: a routine successful cycle).
            kit.States.Set(kit.States.Get(entry.Server.Id) with { LastAttemptAt = Ui4TestKit.Now });
        }

        foreach (var action in queued.ToList())
        {
            action();
        }

        stopwatch.Stop();
        Assert.Single(queued);
        output.WriteLine($"500-server cycle: {stopwatch.Elapsed.TotalMilliseconds:0.0} ms, dashboard notifications {dashboardNotifications}, directory notifications {directoryNotifications}");

        // Nothing visible changed in this cycle: the overview must not re-announce its aggregates per server.
        Assert.True(dashboardNotifications <= 20, $"dashboard raised {dashboardNotifications} notifications");
        Assert.True(directoryNotifications <= 5, $"directory raised {directoryNotifications} notifications");
    }
}
