using System.Runtime.InteropServices;

namespace ServerMonitor.App.Shell.Tray;

/// <summary>
/// The Win32 surface the owned tray boundary needs, and nothing else.
/// <para>
/// Kept separate from the two callers so the boundary is one auditable file: every entry point the
/// shell code can reach is declared here, so "what does this slice call into Windows?" is answered by
/// reading a single list rather than by grepping.
/// </para>
/// </summary>
internal static class NativeTrayInterop
{
    // DllImport throughout, not LibraryImport: the source generator requires AllowUnsafeBlocks for the
    // whole project, and turning unsafe code on application-wide to save a marshalling stub is a far
    // larger change than the one it buys.

    // ------------------------------------------------------------------ Shell_NotifyIcon

    internal const uint NIM_ADD = 0x00000000;
    internal const uint NIM_MODIFY = 0x00000001;
    internal const uint NIM_DELETE = 0x00000002;
    internal const uint NIM_SETVERSION = 0x00000004;

    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON = 0x00000002;
    internal const uint NIF_TIP = 0x00000004;
    internal const uint NIF_SHOWTIP = 0x00000080;

    /// <summary>
    /// Version 4 is REQUIRED, never best-effort. Under v4 the callback carries the icon id in the high
    /// word of lParam and screen coordinates in wParam, which is what makes the CV-1 contract decidable.
    /// A silent fall back to v3 would change the meaning of every parameter the contract validates.
    /// </summary>
    internal const uint NOTIFYICON_VERSION_4 = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATAW
    {
        internal uint cbSize;
        internal nint hWnd;
        internal uint uID;
        internal uint uFlags;
        internal uint uCallbackMessage;
        internal nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string szTip;

        internal uint dwState;
        internal uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string szInfo;

        /// <summary>Union of uTimeout and uVersion. Only ever used as uVersion here.</summary>
        internal uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        internal string szInfoTitle;

        internal uint dwInfoFlags;
        internal Guid guidItem;
        internal nint hBalloonIcon;
    }

    // DllImport, not LibraryImport: NOTIFYICONDATAW carries three fixed-size string buffers, so it is
    // not blittable and the source generator cannot marshal it without an unsafe fixed-buffer rewrite.
    // The runtime marshaller handles ByValTStr correctly; there is nothing to gain from forcing it.
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    // ------------------------------------------------------------------ window class and messages

    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_OVERLAPPED = 0x00000000;
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_DPICHANGED = 0x02E0;

    internal delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        internal uint cbSize;
        internal uint style;
        internal nint lpfnWndProc;
        internal int cbClsExtra;
        internal int cbWndExtra;
        internal nint hInstance;
        internal nint hIcon;
        internal nint hCursor;
        internal nint hbrBackground;
        internal nint lpszMenuName;
        internal nint lpszClassName;
        internal nint hIconSm;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    internal static extern nint CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint hWnd);

    /// <summary>
    /// The shell's broadcast when the taskbar is recreated. The id is resolved at runtime and is not a
    /// constant: a hard-coded value would be a different message on a different session.
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandleW(string? lpModuleName);

    // ------------------------------------------------------------------ DPI-correct icon

    internal const uint IMAGE_ICON = 1;
    internal const uint LR_LOADFROMFILE = 0x00000010;
    internal const uint LR_DEFAULTCOLOR = 0x00000000;
    internal const int SM_CXSMICON = 49;
    internal const int SM_CYSMICON = 50;
    internal const uint USER_DEFAULT_SCREEN_DPI = 96;

    [DllImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    internal static extern nint LoadImageW(
        nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", EntryPoint = "GetSystemMetricsForDpi", SetLastError = true)]
    internal static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    internal static extern uint GetDpiForWindow(nint hWnd);

    // ------------------------------------------------------------------ anchor sanitisation

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int x;
        internal int y;
    }

    internal const uint MONITOR_DEFAULTTONULL = 0x00000000;

    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    internal static extern nint MonitorFromPoint(POINT pt, uint dwFlags);
}
