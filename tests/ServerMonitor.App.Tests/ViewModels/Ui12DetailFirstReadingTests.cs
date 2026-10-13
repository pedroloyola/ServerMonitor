using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.12 UI12A-STATE-05 (Prism, P2). While the FIRST collection of a newly added server ran, the Detail had neither
/// IsFirstReading (the card's IsMetricsPending excludes a running refresh) nor HasReading: no skeleton, "—" in the strip and
/// the status went from "A ligar…" to "Sem dados" - contradicting a collection in progress. The first reading now stays
/// "A ligar…" + skeleton until a snapshot or an error arrives. Runtime timing window: Beacon (Prism R-4).
/// </summary>
public sealed class Ui12DetailFirstReadingTests
{
    [Fact]
    public async Task WhileTheFirstCollectionRuns_ItIsStillTheFirstReading()
    {
        var fleet = new Ui4TestKit.Fleet().Add("new", ServerHealth.Unknown, snapshot: false);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "new");
        Assert.True(detail.IsFirstReading);
        var raised = new List<string?>();
        detail.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        kit.States.Set(kit.States.Get(fleet.IdOf("new")) with { IsRefreshing = true });

        Assert.True(detail.IsRefreshing);
        Assert.True(detail.IsFirstReading);
        Assert.False(detail.HasReading);
        Assert.Equal(kit.Localization.GetString("ServerDetailConnecting"), detail.StatusLabel);
    }

    [Fact]
    public async Task AFailedFirstCollection_EndsTheFirstReading_WithTheError()
    {
        var fleet = new Ui4TestKit.Fleet().Add("new", ServerHealth.Unknown, snapshot: false);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "new");

        kit.States.Set(kit.States.Get(fleet.IdOf("new")) with
        {
            IsRefreshing = false,
            LastError = MetricsCollectionErrorCode.ConnectionFailed,
            LastAttemptAt = Ui4TestKit.Now,
        });

        Assert.False(detail.IsFirstReading);
        Assert.True(detail.HasCollectionError);
    }

    [Fact]
    public async Task AReading_EndsTheFirstReading()
    {
        var fleet = new Ui4TestKit.Fleet().Add("ok", ServerHealth.Healthy, 10, 20, 30);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "ok");
        kit.States.Set(kit.States.Get(fleet.IdOf("ok")) with { IsRefreshing = true });

        Assert.False(detail.IsFirstReading);
        Assert.True(detail.HasReading);
    }

    private static ServerDetailViewModel Open(Ui4TestKit.Harness kit, string name)
    {
        var detail = new ServerDetailViewModel(
            kit.Dashboard, kit.Navigation, kit.Localization, null, null, monitoringOptions: null,
            new PresentationClock(new FakeTimeProvider(Ui4TestKit.Now)));
        detail.Load(kit.Dashboard.VisibleServers.Single(card => card.Name == name).Server.Id, ServerDetailOrigin.Overview);
        return detail;
    }
}
