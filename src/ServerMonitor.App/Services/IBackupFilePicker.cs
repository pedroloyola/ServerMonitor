namespace ServerMonitor.App.Services;

/// <summary>
/// The Save/Open pickers for encrypted configuration backups (M14.6). Both return a plain file-system
/// path, or <see langword="null"/> when the user cancels; neither opens, reads or writes the file.
/// </summary>
public interface IBackupFilePicker
{
    /// <summary>The user confirms any overwrite in the picker itself.</summary>
    Task<string?> PickSaveAsync(
        string suggestedFileName,
        string fileTypeLabel,
        CancellationToken cancellationToken = default);

    Task<string?> PickOpenAsync(CancellationToken cancellationToken = default);
}
