using Windows.Storage.Pickers;

namespace ServerMonitor.App.Services;

public sealed class PrivateKeyFilePicker(IWindowContext windowContext) : IPrivateKeyFilePicker
{
    public async Task<string?> PickAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowContext.WindowHandle);

        var file = await picker.PickSingleFileAsync().AsTask(cancellationToken);
        return file?.Path;
    }
}
