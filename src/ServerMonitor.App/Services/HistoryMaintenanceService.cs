using ServerMonitor.Core.History;

namespace ServerMonitor.App.Services;

/// <summary>
/// Clears local history after an explicit, destructive-styled confirmation (spec §33). Touches only
/// the history database; it also resets the recorder's per-server cadence so the next cycle records
/// immediately. Never deletes servers, credentials, known hosts, ignored devices or settings.
/// UI.5: holds no UI — the confirmations come from <see cref="IHistoryMaintenanceInteraction"/>.
/// Every registered <see cref="IHistoryParticipant"/> is cleared or reset after the built-in history, and the
/// <see cref="HistoryGeneration"/> advances first so late results of the old generation are dropped.
/// </summary>
public sealed class HistoryMaintenanceService : IHistoryMaintenanceService
{
    private readonly IServerHistoryStore _store;
    private readonly Func<Task<bool>> _clear;
    private readonly Func<Task<bool>> _reset;
    private readonly Action _forgetCadence;
    private readonly IHistoryMaintenanceInteraction _interaction;
    private readonly IHistoryParticipant[] _participants;
    private readonly HistoryGeneration? _generation;

    public HistoryMaintenanceService(
        IServerHistoryStore store,
        HistoryWriterService writer,
        HistoryRecorder recorder,
        IHistoryMaintenanceInteraction interaction,
        IEnumerable<IHistoryParticipant>? participants = null,
        HistoryGeneration? generation = null)
        : this(
            store,
            () => writer.ClearAsync(),
            () => writer.ResetAsync(),
            () => recorder.ForgetAll(),
            interaction,
            participants,
            generation)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(recorder);
    }

    /// <summary>TEST SEAM: the real decision logic over the writer's clear/reset and the recorder's cadence reset.</summary>
    internal HistoryMaintenanceService(
        IServerHistoryStore store,
        Func<Task<bool>> clear,
        Func<Task<bool>> reset,
        Action forgetCadence,
        IHistoryMaintenanceInteraction interaction,
        IEnumerable<IHistoryParticipant>? participants = null,
        HistoryGeneration? generation = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clear = clear ?? throw new ArgumentNullException(nameof(clear));
        _reset = reset ?? throw new ArgumentNullException(nameof(reset));
        _forgetCadence = forgetCadence ?? throw new ArgumentNullException(nameof(forgetCadence));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _participants = participants?.ToArray() ?? [];
        _generation = generation;
    }

    public bool IsAvailable => _store.IsAvailable;

    public async Task<HistoryClearOutcome> ClearHistoryWithConfirmationAsync()
    {
        if (!await _interaction.ConfirmClearHistoryAsync().ConfigureAwait(true))
        {
            return HistoryClearOutcome.Cancelled;
        }

        if (!_store.IsAvailable)
        {
            return HistoryClearOutcome.Unavailable;
        }

        // A new generation first, so a probe result still on its way can no longer land in the cleared history.
        _generation?.Advance();
        if (!await _clear().ConfigureAwait(true))
        {
            return HistoryClearOutcome.Unavailable;
        }

        _forgetCadence();
        return await RunParticipantsAsync(static participant => participant.ClearAsync()).ConfigureAwait(true)
            ? HistoryClearOutcome.Cleared
            : HistoryClearOutcome.Unavailable;
    }

    public async Task<HistoryResetOutcome> ResetHistoryWithConfirmationAsync()
    {
        if (!await _interaction.ConfirmResetHistoryAsync().ConfigureAwait(true))
        {
            return HistoryResetOutcome.Cancelled;
        }

        _generation?.Advance();
        if (!await _reset().ConfigureAwait(true))
        {
            return HistoryResetOutcome.Unavailable;
        }

        _forgetCadence();
        return await RunParticipantsAsync(static participant => participant.ResetAsync()).ConfigureAwait(true)
            ? HistoryResetOutcome.Reset
            : HistoryResetOutcome.Unavailable;
    }

    /// <summary>Asks every participant, even after one fails; true only when all of them finished.</summary>
    private async Task<bool> RunParticipantsAsync(Func<IHistoryParticipant, Task<bool>> run)
    {
        var allFinished = true;
        foreach (var participant in _participants)
        {
            try
            {
                allFinished &= await run(participant).ConfigureAwait(true);
            }
            catch (Exception)
            {
                allFinished = false;
            }
        }

        return allFinished;
    }
}

/// <summary>Registered by default so Settings resolves even when the real history stack is absent
/// (QA harnesses): reports unavailable and clears nothing.</summary>
public sealed class NullHistoryMaintenanceService : IHistoryMaintenanceService
{
    public bool IsAvailable => false;

    public Task<HistoryClearOutcome> ClearHistoryWithConfirmationAsync() =>
        Task.FromResult(HistoryClearOutcome.Unavailable);

    public Task<HistoryResetOutcome> ResetHistoryWithConfirmationAsync() =>
        Task.FromResult(HistoryResetOutcome.Unavailable);
}
