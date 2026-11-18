using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.Core.History;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// M16.1: a history clear or reset moves the history generation forward first, clears the built-in history, then asks
/// every registered participant to do the same. A participant that fails or throws makes the outcome unavailable,
/// without stopping the others.
/// </summary>
public sealed class HistoryParticipantTests
{
    [Fact]
    public async Task Clear_AdvancesTheGeneration_ThenAsksEveryParticipant()
    {
        var order = new List<string>();
        var generation = new HistoryGeneration();
        generation.Advanced += (_, _) => order.Add("generation");
        var first = new Participant("first", order);
        var second = new Participant("second", order);
        var service = Service(order, [first, second], generation);

        var outcome = await service.ClearHistoryWithConfirmationAsync();

        Assert.Equal(HistoryClearOutcome.Cleared, outcome);
        Assert.Equal(["generation", "built-in clear", "first clear", "second clear"], order);
        Assert.Equal(1, generation.Current);
    }

    [Fact]
    public async Task Reset_AdvancesTheGeneration_ThenAsksEveryParticipant()
    {
        var order = new List<string>();
        var generation = new HistoryGeneration();
        generation.Advanced += (_, _) => order.Add("generation");
        var service = Service(order, [new Participant("only", order)], generation);

        var outcome = await service.ResetHistoryWithConfirmationAsync();

        Assert.Equal(HistoryResetOutcome.Reset, outcome);
        Assert.Equal(["generation", "built-in reset", "only reset"], order);
    }

    [Fact]
    public async Task AParticipantThatFailsOrThrows_MakesItUnavailable_AndTheOthersStillRun()
    {
        var order = new List<string>();
        var failing = new Participant("failing", order) { Finishes = false };
        var throwing = new Participant("throwing", order) { Throws = true };
        var last = new Participant("last", order);
        var service = Service(order, [failing, throwing, last], new HistoryGeneration());

        Assert.Equal(HistoryClearOutcome.Unavailable, await service.ClearHistoryWithConfirmationAsync());
        Assert.Equal(HistoryResetOutcome.Unavailable, await service.ResetHistoryWithConfirmationAsync());
        Assert.Equal(2, last.Calls);
    }

    [Fact]
    public async Task WithoutParticipants_TheOutcomeIsUnchanged()
    {
        var order = new List<string>();
        var service = Service(order, [], generation: null);

        Assert.Equal(HistoryClearOutcome.Cleared, await service.ClearHistoryWithConfirmationAsync());
        Assert.Equal(HistoryResetOutcome.Reset, await service.ResetHistoryWithConfirmationAsync());
        Assert.Equal(["built-in clear", "built-in reset"], order);
    }

    [Fact]
    public async Task ACancelledConfirmation_TouchesNothing()
    {
        var order = new List<string>();
        var generation = new HistoryGeneration();
        var participant = new Participant("only", order);
        var service = Service(order, [participant], generation, confirm: false);

        Assert.Equal(HistoryClearOutcome.Cancelled, await service.ClearHistoryWithConfirmationAsync());
        Assert.Equal(HistoryResetOutcome.Cancelled, await service.ResetHistoryWithConfirmationAsync());
        Assert.Empty(order);
        Assert.Equal(0, generation.Current);
    }

    [Fact]
    public async Task AFailedBuiltInClear_DoesNotAskTheParticipants()
    {
        var order = new List<string>();
        var participant = new Participant("only", order);
        var service = new HistoryMaintenanceService(
            new FakeServerHistoryStore { Available = true },
            () => Task.FromResult(false),
            () => Task.FromResult(false),
            () => { },
            new Interaction(confirm: true),
            [participant],
            new HistoryGeneration());

        Assert.Equal(HistoryClearOutcome.Unavailable, await service.ClearHistoryWithConfirmationAsync());
        Assert.Equal(HistoryResetOutcome.Unavailable, await service.ResetHistoryWithConfirmationAsync());
        Assert.Equal(0, participant.Calls);
    }

    private static HistoryMaintenanceService Service(
        List<string> order,
        IHistoryParticipant[] participants,
        HistoryGeneration? generation,
        bool confirm = true) =>
        new(
            new FakeServerHistoryStore { Available = true },
            () =>
            {
                order.Add("built-in clear");
                return Task.FromResult(true);
            },
            () =>
            {
                order.Add("built-in reset");
                return Task.FromResult(true);
            },
            () => { },
            new Interaction(confirm),
            participants,
            generation);

    private sealed class Interaction(bool confirm) : IHistoryMaintenanceInteraction
    {
        public Task<bool> ConfirmClearHistoryAsync() => Task.FromResult(confirm);

        public Task<bool> ConfirmResetHistoryAsync() => Task.FromResult(confirm);
    }

    private sealed class Participant(string name, List<string> order) : IHistoryParticipant
    {
        public bool Finishes { get; init; } = true;

        public bool Throws { get; init; }

        public int Calls { get; private set; }

        public Task<bool> ClearAsync(CancellationToken cancellationToken = default) => Run("clear");

        public Task<bool> ResetAsync(CancellationToken cancellationToken = default) => Run("reset");

        private Task<bool> Run(string what)
        {
            Calls++;
            order.Add($"{name} {what}");
            return Throws ? throw new InvalidOperationException() : Task.FromResult(Finishes);
        }
    }
}
