using System.IO;
using ServerMonitor.App.ViewModels;
using Windows.Storage.Pickers;

namespace ServerMonitor.App.Services;

public sealed class BackupFilePicker(IWindowContext windowContext) : IBackupFilePicker
{
    public async Task<string?> PickSaveAsync(
        string suggestedFileName,
        string fileTypeLabel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedFileName,
            DefaultFileExtension = BackupRestoreViewModel.BackupFileExtension
        };
        picker.FileTypeChoices.Add(fileTypeLabel, [BackupRestoreViewModel.BackupFileExtension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowContext.WindowHandle);

        var file = await picker.PickSaveFileAsync().AsTask(cancellationToken);
        if (file is null || string.IsNullOrEmpty(file.Path))
        {
            return null;
        }

        RemoveEmptyPlaceholder(file.Path);
        return file.Path;
    }

    public async Task<string?> PickOpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(BackupRestoreViewModel.BackupFileExtension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowContext.WindowHandle);

        var file = await picker.PickSingleFileAsync().AsTask(cancellationToken);
        return string.IsNullOrEmpty(file?.Path) ? null : file.Path;
    }

    // The save picker leaves a zero-byte file at a new destination. Removing it keeps a failed or
    // canceled export from leaving an empty "backup" behind; a file with content is never touched.
    private static void RemoveEmptyPlaceholder(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length == 0)
            {
                info.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
