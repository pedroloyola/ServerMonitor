using ServerMonitor.Core.History;

namespace ServerMonitor.App.Services;

/// <summary>
/// Clears local history after an explicit, destructive-styled confirmation (spec §33). Touches only
/// the history database; it also resets the recorder's per-server cadence so the next cycle records
/// immediately. Never deletes servers, credentials, known hosts, ignored devices or settings.
/// UI.5: holds no UI — the confirmations come from <see cref="IHistoryMaintenanceInteraction"/>.
/// </summary>
public sealed class HistoryMaintenanceService : IHistoryMaintenanceService
{
    private readonly IServerHistoryStore _store;
    private readonly Func<Task<bool>> _clear;
    private readonly Func<Task<bool>> _reset;
    private readonly Action _forgetCadence;
    private readonly IHistoryMaintenanceInteraction _interaction;

    public HistoryMaintenanceService(
        IServerHistoryStore store,
        HistoryWriterService writer,
        HistoryRecorder recorder,
        IHistoryMaintenanceInteraction interaction)
        : this(store, () => writer.ClearAsync(), () => writer.ResetAsync(), () => recorder.ForgetAll(), interaction)
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
        IHistoryMaintenanceInteraction interaction)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clear = clear ?? throw new ArgumentNullException(nameof(clear));
        _reset = reset ?? throw new ArgumentNullException(nameof(reset));
        _forgetCadence = forgetCadence ?? throw new ArgumentNullException(nameof(forgetCadence));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
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

        if (!await _clear().ConfigureAwait(true))
        {
            return HistoryClearOutcome.Unavailable;
        }

        _forgetCadence();
        return HistoryClearOutcome.Cleared;
    }

    public async Task<HistoryResetOutcome> ResetHistoryWithConfirmationAsync()
    {
        if (!await _interaction.ConfirmResetHistoryAsync().ConfigureAwait(true))
        {
            return HistoryResetOutcome.Cancelled;
        }

        if (!await _reset().ConfigureAwait(true))
        {
            return HistoryResetOutcome.Unavailable;
        }

        _forgetCadence();
        return HistoryResetOutcome.Reset;
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
