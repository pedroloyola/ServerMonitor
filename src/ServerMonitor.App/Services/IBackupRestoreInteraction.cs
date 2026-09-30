using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Backup;

namespace ServerMonitor.App.Services;

/// <summary>
/// The modal surfaces of backup and restore (M14.6). Every string is already localized by
/// <see cref="BackupRestoreViewModel"/>; an implementation only lays it out, so the whole flow runs in
/// a test against a scripted fake.
/// </summary>
public interface IBackupRestoreInteraction
{
    /// <summary>Returns when the dialog closes; the session's submit decides whether it may.</summary>
    Task ShowCreateDialogAsync(BackupCreateSession session);

    /// <summary>Returns when the dialog closes; the session's submit decides whether it may.</summary>
    Task ShowRestoreOpenDialogAsync(RestoreOpenSession session);

    /// <summary>The destructive confirm. <see langword="true"/> only for the explicit primary action.</summary>
    Task<bool> ConfirmRestoreAsync(RestoreConfirmation confirmation);

    /// <summary>Keeps a modal, non-dismissable "Restoring…" surface up while <paramref name="apply"/> runs.</summary>
    Task<RestoreApplyResult> ShowRestoringAsync(string message, Func<Task<RestoreApplyResult>> apply);

    /// <summary>Returns once the user acknowledged; the caller then exits the app.</summary>
    Task ShowRestoreCompletedAsync(string title, string message, string primaryText);

    /// <summary>A one-off notice whose text the user can select (the stuck-journal folder path).</summary>
    Task ShowNoticeAsync(string title, string message, string closeText);
}
