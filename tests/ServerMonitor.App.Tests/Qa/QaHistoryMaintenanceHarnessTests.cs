using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.App.Qa;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI.5 fix round 2 (Beacon C1 N9): the <c>data*</c> overview scenarios compose an in-memory history-maintenance double
/// over the REAL confirmation interaction, so the Limpar / Repor dialogs can be exercised at runtime. The double confirms
/// first (like the real service), never touches a disk, and the other scenarios keep the inert default.
/// </summary>
public sealed class QaHistoryMaintenanceHarnessTests
{
    [Theory]
    [InlineData("data", true)]
    [InlineData("data-failing", false)]
    public void TheDataScenarios_ComposeTheDouble_ByType(string scenario, bool succeeds)
    {
        var services = new ServiceCollection();
        QaOverviewComposition.Apply(services, scenario, backupDoublesRequested: true);

        var maintenance = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHistoryMaintenanceService));
        Assert.Equal(typeof(QaHistoryMaintenanceService), maintenance.ImplementationType);
        var mode = (QaHistoryMaintenanceMode)Assert.Single(services, descriptor => descriptor.ServiceType == typeof(QaHistoryMaintenanceMode)).ImplementationInstance!;
        Assert.Equal(succeeds, mode.OperationsSucceed);
    }

    [Theory]
    [InlineData("mixed")]
    [InlineData("detail")]
    public void OtherScenarios_KeepTheInertDefault(string scenario)
    {
        var services = new ServiceCollection();
        QaOverviewComposition.Apply(services, scenario, backupDoublesRequested: true);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHistoryMaintenanceService));
    }

    [Fact]
    public async Task TheDouble_ConfirmsFirst_ThenSucceedsOrFails_AsItsScenario()
    {
        var cancelling = new Interaction(confirm: false);
        var data = new QaHistoryMaintenanceService(cancelling, new QaHistoryMaintenanceMode(true));
        Assert.Equal(HistoryClearOutcome.Cancelled, await data.ClearHistoryWithConfirmationAsync());
        Assert.Equal(HistoryResetOutcome.Cancelled, await data.ResetHistoryWithConfirmationAsync());
        Assert.Equal((1, 1), (cancelling.Clears, cancelling.Resets));

        var confirming = new Interaction(confirm: true);
        Assert.True(new QaHistoryMaintenanceService(confirming, new QaHistoryMaintenanceMode(true)).IsAvailable);
        Assert.Equal(HistoryClearOutcome.Cleared, await new QaHistoryMaintenanceService(confirming, new QaHistoryMaintenanceMode(true)).ClearHistoryWithConfirmationAsync());

        var failing = new QaHistoryMaintenanceService(confirming, new QaHistoryMaintenanceMode(false));
        Assert.False(failing.IsAvailable); // "Repor histórico" is offered
        Assert.Equal(HistoryClearOutcome.Unavailable, await failing.ClearHistoryWithConfirmationAsync());
        Assert.Equal(HistoryResetOutcome.Unavailable, await failing.ResetHistoryWithConfirmationAsync());
    }

    private sealed class Interaction(bool confirm) : IHistoryMaintenanceInteraction
    {
        public int Clears { get; private set; }

        public int Resets { get; private set; }

        public Task<bool> ConfirmClearHistoryAsync()
        {
            Clears++;
            return Task.FromResult(confirm);
        }

        public Task<bool> ConfirmResetHistoryAsync()
        {
            Resets++;
            return Task.FromResult(confirm);
        }
    }
}
