using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.4 Beacon QA r1 (.boss/tmp/ui4/beacon/qa-r1.md): the way back keeps the user's place (SHOULD-1) and a failed
/// Ocultar/Remover started on the interim page is reported there (SHOULD-3).
/// </summary>
public sealed class Ui4BeaconTests
{
    private static async Task<Ui4TestKit.Harness> LoadedAsync(Ui4TestKit.Fleet fleet)
    {
        var kit = Ui4TestKit.Create(fleet);
        await kit.Dashboard.LoadAsync();
        return kit;
    }

    private static Ui4TestKit.Fleet Mixed() => new Ui4TestKit.Fleet()
        .Add("web", ServerHealth.Healthy, 10, 20, 30)
        .Add("db", ServerHealth.Critical, 10, 20, 97)
        .Add("cache", ServerHealth.Healthy, 10, 20, 30);

    [Fact]
    public async Task OpeningASummaryRow_ReturnsFocusToThatRow_Once()
    {
        var kit = await LoadedAsync(Mixed());
        var row = kit.Dashboard.OverviewServers.Single(r => r.ServerId == Mixed2Id(kit, "cache"));

        row.OpenDetailCommand.Execute(null);

        Assert.Equal((OverviewReturnTarget.ServerRow, row.ServerId), kit.Dashboard.TakeReturnFocus());
        Assert.Equal((OverviewReturnTarget.None, Guid.Empty), kit.Dashboard.TakeReturnFocus());
    }

    [Fact]
    public async Task OpeningThePriorityProblem_ReturnsFocusToThePriorityCard()
    {
        var kit = await LoadedAsync(Mixed());
        var problem = Assert.IsType<PriorityProblem>(kit.Dashboard.PriorityProblem);

        kit.Dashboard.OpenPriorityProblemCommand.Execute(null);

        Assert.Equal((OverviewReturnTarget.Priority, problem.ServerId), kit.Dashboard.TakeReturnFocus());
    }

    [Fact]
    public async Task ViewAll_ReturnsFocusToViewAll()
    {
        var kit = await LoadedAsync(Mixed());

        kit.Dashboard.ViewAllServersCommand.Execute(null);

        Assert.Equal(OverviewReturnTarget.ViewAll, kit.Dashboard.TakeReturnFocus().Target);
    }

    /// <summary>The Servidores page is per visit: the NEXT one refocuses the row that opened the interim page.</summary>
    [Fact]
    public async Task ADirectoryRow_IsRefocusedByTheNextServersPage_Once()
    {
        var kit = await LoadedAsync(Mixed());
        using (var first = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake()))
        {
            first.Rows[1].OpenDetailCommand.Execute(null);
        }

        using var second = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());

        Assert.Equal(1, second.TakeReturnFocusIndex());
        Assert.Equal(-1, second.TakeReturnFocusIndex());
        Assert.Equal(OverviewReturnTarget.None, kit.Dashboard.TakeReturnFocus().Target);
    }

    /// <summary>SHOULD-3: Ocultar fails on the interim page → its InfoBar opens (the dashboard's error, one source).</summary>
    [Fact]
    public async Task AFailedHideOnTheInterimPage_IsReportedThere_AndClosingItClosesTheSource()
    {
        var kit = await LoadedAsync(Mixed());
        var id = Mixed2Id(kit, "web");
        using var detail = new ServerDetailViewModel(kit.Dashboard, kit.Navigation, kit.Localization);
        detail.Load(id, ServerDetailOrigin.Overview);
        var raised = new List<string?>();
        detail.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // FakeServerService.HideAsync throws synchronously: the dashboard's existing error path reports it.
        detail.Card!.HideCommand.Execute(null);

        Assert.True(detail.IsOperationErrorOpen);
        Assert.Contains(nameof(ServerDetailViewModel.IsOperationErrorOpen), raised);

        detail.IsOperationErrorOpen = false;
        Assert.False(kit.Dashboard.IsOperationErrorOpen);
    }

    [Fact]
    public async Task ADisposedInterimPage_NoLongerFollowsTheDashboardError()
    {
        var kit = await LoadedAsync(Mixed());
        var detail = new ServerDetailViewModel(kit.Dashboard, kit.Navigation, kit.Localization);
        detail.Load(Mixed2Id(kit, "web"), ServerDetailOrigin.Overview);
        var raised = 0;
        detail.PropertyChanged += (_, _) => raised++;
        detail.Dispose();

        kit.Dashboard.IsOperationErrorOpen = true;

        Assert.Equal(0, raised);
    }

    private static Guid Mixed2Id(Ui4TestKit.Harness kit, string name) =>
        kit.Dashboard.VisibleServers.Single(card => card.Server.Name == name).Server.Id;
}
