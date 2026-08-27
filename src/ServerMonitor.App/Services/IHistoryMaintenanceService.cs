namespace ServerMonitor.App.Services;

/// <summary>Result of a Clear-history request (spec §33).</summary>
public enum HistoryClearOutcome
{
    /// <summary>The user dismissed the confirmation; nothing was deleted.</summary>
    Cancelled,

    /// <summary>History was cleared.</summary>
    Cleared,

    /// <summary>The history store is unavailable (e.g. corrupt), so nothing could be cleared.</summary>
    Unavailable
}

public enum HistoryResetOutcome
{
    Cancelled,
    Reset,
    Unavailable
}

/// <summary>
/// Destructive history maintenance from Settings. Keeps storage logic out of the ViewModel: the VM
/// invokes this, which shows an explicit confirmation and clears only history data — never servers,
/// credentials, known hosts, ignored devices or settings (spec §33).
/// </summary>
public interface IHistoryMaintenanceService
{
    bool IsAvailable { get; }

    Task<HistoryClearOutcome> ClearHistoryWithConfirmationAsync();

    Task<HistoryResetOutcome> ResetHistoryWithConfirmationAsync();
}
