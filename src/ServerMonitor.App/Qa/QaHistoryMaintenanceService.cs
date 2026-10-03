using ServerMonitor.App.Services;

namespace ServerMonitor.App.Qa;

/// <summary>
/// QA-ONLY (UI.5 fix round 2, Beacon C1 N9): history maintenance for the <c>--qa-overview data*</c> scenarios, so the
/// REAL confirmation dialogs (<see cref="HistoryMaintenanceDialogService"/>) can be exercised and captured. Mirrors
/// <see cref="HistoryMaintenanceService"/>'s order (confirm first, then act) over an in-memory flag: nothing is ever
/// deleted or recreated on disk. <c>data</c>: available, clearing succeeds. <c>data-failing</c>: unavailable, so
/// "Repor histórico" is offered, and every operation fails after its confirmation. Excluded from Release.
/// </summary>
internal sealed class QaHistoryMaintenanceService(IHistoryMaintenanceInteraction interaction, QaHistoryMaintenanceMode mode)
    : IHistoryMaintenanceService
{
    private readonly IHistoryMaintenanceInteraction _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
    private readonly bool operationsSucceed = (mode ?? throw new ArgumentNullException(nameof(mode))).OperationsSucceed;

    public bool IsAvailable => operationsSucceed;

    public async Task<HistoryClearOutcome> ClearHistoryWithConfirmationAsync()
    {
        if (!await _interaction.ConfirmClearHistoryAsync().ConfigureAwait(true))
        {
            return HistoryClearOutcome.Cancelled;
        }

        return operationsSucceed ? HistoryClearOutcome.Cleared : HistoryClearOutcome.Unavailable;
    }

    public async Task<HistoryResetOutcome> ResetHistoryWithConfirmationAsync()
    {
        if (!await _interaction.ConfirmResetHistoryAsync().ConfigureAwait(true))
        {
            return HistoryResetOutcome.Cancelled;
        }

        return operationsSucceed ? HistoryResetOutcome.Reset : HistoryResetOutcome.Unavailable;
    }
}

/// <summary>QA-ONLY: whether the data scenario's history operations succeed (data) or fail (data-failing).</summary>
internal sealed record QaHistoryMaintenanceMode(bool OperationsSucceed);
