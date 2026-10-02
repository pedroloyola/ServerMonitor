using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.4 Atlas r1 (.boss/tmp/ui4/atlas/review-r1.md): guarantees the suite did not hold — the coalesced recompute
/// re-arms after each burst, a failed first load never leaves the skeletons up, singleton pages are never released by
/// navigation, and the rows colour metrics with the engine's very thresholds instance.
/// </summary>
public sealed class Ui4AtlasTests
{
    /// <summary>MUST-1: every burst recomputes — the schedule re-arms after the queued pass (A1 counterproof).</summary>
    [Fact]
    public async Task TheCoalescedRecompute_RearmsAfterEachBurst()
    {
        var fleet = new Ui4TestKit.Fleet();
        for (var i = 0; i < 10; i++)
        {
            fleet.Add($"s-{i}", ServerHealth.Healthy, 10, 10, 10);
        }

        var kit = Ui4TestKit.Create(fleet);
        await kit.Dashboard.LoadAsync();
        var queued = new List<Action>();
        kit.Dashboard.OverviewScheduler = action =>
        {
            queued.Add(action);
            return true;
        };

        foreach (var entry in fleet.Entries)
        {
            kit.States.Set(kit.States.Get(entry.Server.Id) with { Health = ServerHealth.Offline });
        }

        Assert.Single(queued);
        queued[0]();
        Assert.Equal(10, kit.Dashboard.HealthSummary.Offline);

        // Second burst: back to healthy. It must queue again and, once run, be reflected.
        foreach (var entry in fleet.Entries)
        {
            kit.States.Set(kit.States.Get(entry.Server.Id) with { Health = ServerHealth.Healthy });
        }

        Assert.Equal(2, queued.Count);
        queued[1]();
        Assert.Equal(10, kit.Dashboard.HealthSummary.Healthy);
        Assert.Equal(0, kit.Dashboard.HealthSummary.Offline);
    }

    /// <summary>MUST-2: a failing first load ends the loading state (A2 counterproof) — no eternal skeletons.</summary>
    [Fact]
    public async Task AFailedFirstLoad_EndsLoading_AndShowsTheExistingError()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet(), new ResWLocalizationService("pt-PT"));
        kit.Servers.GetAllOverride = _ => Task.FromException<IReadOnlyList<Server>>(new IOException("disk"));
        using var directory = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization);
        Assert.True(kit.Dashboard.IsLoading);
        Assert.Equal("A carregar os teus servidores…", directory.HeaderContextDisplay);

        await kit.Dashboard.LoadAsync();

        Assert.False(kit.Dashboard.IsLoading);
        Assert.NotEqual("A recolher dados…", kit.Dashboard.HeaderContextDisplay);
        Assert.True(kit.Dashboard.IsOperationErrorOpen);
        Assert.False(directory.IsLoading);
        Assert.NotEqual("A carregar os teus servidores…", directory.HeaderContextDisplay);
    }

    /// <summary>SHOULD-1: navigation releases the replaced page when it is IDisposable — the singletons must never be.</summary>
    [Fact]
    public void SingletonPages_AreNeverDisposable_PerVisitPagesAre()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(DashboardPage)));
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(SettingsPage)));
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(ServersPage)));
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(ServerDetailPage)));
    }

    /// <summary>SHOULD-2: the rows colour metrics with the engine's thresholds instance, not a default copy (A4).</summary>
    [Fact]
    public async Task Rows_UseTheComposedThresholds_InTheSummaryListAndTheDirectory()
    {
        var options = new MonitoringOptions { Thresholds = MonitoringThresholds.Default with { DiskWarning = 50, DiskCritical = 60 } };
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("db", ServerHealth.Critical, 10, 10, 55), options: options);
        await kit.Dashboard.LoadAsync();
        using var directory = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization);

        Assert.Same(options.Thresholds, kit.Dashboard.Thresholds);
        Assert.Equal(ServerHealth.Warning, kit.Dashboard.OverviewServers.Single().DiskSeverity);
        Assert.Equal(ServerHealth.Warning, directory.Rows.Single().DiskSeverity);
    }
}
