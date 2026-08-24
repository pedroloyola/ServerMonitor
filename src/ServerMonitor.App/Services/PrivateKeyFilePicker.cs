using System.IO;
using System.Runtime.InteropServices;
using Windows.Storage.Pickers;

namespace ServerMonitor.App.Services;

public sealed class PrivateKeyFilePicker(IWindowContext windowContext) : IPrivateKeyFilePicker
{
    private const int HResultOk = 0;
    private const int HResultCancelled = unchecked((int)0x800704C7); // HRESULT_FROM_WIN32(ERROR_CANCELLED)
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosFileMustExist = 0x00001000;
    private const uint SigdnFileSystemPath = 0x80058000;

    public async Task<string?> PickAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Try modern native COM IFileOpenDialog which allows setting initial directory directly to %USERPROFILE%\.ssh
        var (succeeded, path) = TryPickViaNativeDialog(windowContext.WindowHandle);
        if (succeeded)
        {
            return path;
        }

        // 2. Fallback to WinRT FileOpenPicker if COM dialog was unavailable
        return await PickViaWinRtPickerAsync(cancellationToken).ConfigureAwait(true);
    }

    private static (bool Handled, string? Path) TryPickViaNativeDialog(nint windowHandle)
    {
        try
        {
            var dialog = (IFileOpenDialog)new FileOpenDialogRCW();
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var sshDir = Path.Combine(userProfile, ".ssh");
            var initialDir = Directory.Exists(sshDir) ? sshDir : userProfile;

            if (Directory.Exists(initialDir))
            {
                var iidShellItem = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(initialDir, 0, in iidShellItem, out var shellItem) == HResultOk && shellItem is not null)
                {
                    dialog.SetFolder(shellItem);
                    dialog.SetDefaultFolder(shellItem);
                }
            }

            dialog.SetOptions(FosForceFileSystem | FosFileMustExist);

            var hr = dialog.Show(windowHandle);
            if (hr == HResultOk)
            {
                if (dialog.GetResult(out var resultItem) == HResultOk && resultItem is not null)
                {
                    if (resultItem.GetDisplayName(SigdnFileSystemPath, out var chosenPath) == HResultOk && !string.IsNullOrWhiteSpace(chosenPath))
                    {
                        return (true, chosenPath);
                    }
                }
                return (true, null);
            }

            if (hr == HResultCancelled)
            {
                return (true, null);
            }

            return (false, null);
        }
        catch
        {
            return (false, null);
        }
    }

    private async Task<string?> PickViaWinRtPickerAsync(CancellationToken cancellationToken)
    {
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        nint pbc,
        in Guid riid,
        out IShellItem? ppv);

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    [ClassInterface(ClassInterfaceType.None)]
    private class FileOpenDialogRCW
    {
    }

    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(nint parent);
        [PreserveSig] int SetFileTypes(uint cFileTypes, nint rgFilterSpec);
        [PreserveSig] int SetFileTypeIndex(uint iFileType);
        [PreserveSig] int GetFileTypeIndex(out uint piFileType);
        [PreserveSig] int Advise(nint pfde, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOptions(uint fos);
        [PreserveSig] int GetOptions(out uint pfos);
        [PreserveSig] int SetDefaultFolder(IShellItem psi);
        [PreserveSig] int SetFolder(IShellItem psi);
        [PreserveSig] int GetFolder(out IShellItem ppsi);
        [PreserveSig] int GetCurrentSelection(out IShellItem ppsi);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        [PreserveSig] int GetResult(out IShellItem? ppsi);
        [PreserveSig] int AddPlace(IShellItem psi, int fdap);
        [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        [PreserveSig] int Close(int hr);
        [PreserveSig] int SetClientGuid(in Guid guid);
        [PreserveSig] int ClearClientData();
        [PreserveSig] int SetFilter(nint pFilter);
        [PreserveSig] int GetResults(out nint ppenum);
        [PreserveSig] int GetSelectedItems(out nint ppsai);
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);
        [PreserveSig] int GetParent(out IShellItem? ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
