namespace ServerMonitor.App.Services;

/// <summary>
/// UI.5 §4: the explicit confirmations of the destructive history actions (Limpar / Repor histórico), moved OUT of
/// <see cref="HistoryMaintenanceService"/> so the service holds no UI and is testable (same split as
/// <see cref="IBackupRestoreInteraction"/>). True only when the user chose the destructive button; a dismissed dialog,
/// or no window to show it in, is false.
/// </summary>
public interface IHistoryMaintenanceInteraction
{
    Task<bool> ConfirmClearHistoryAsync();

    Task<bool> ConfirmResetHistoryAsync();
}
