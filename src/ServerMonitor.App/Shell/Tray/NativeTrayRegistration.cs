using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using static ServerMonitor.App.Shell.Tray.NativeTrayInterop;

namespace ServerMonitor.App.Shell.Tray;

/// <summary>
/// The real <c>Shell_NotifyIcon</c> boundary: the reason this slice exists.
/// <para>
/// WinUIEx 2.9.3 discards the <c>BOOL</c> that <c>NIM_ADD</c> returns, so an application built on it
/// cannot distinguish "the icon is registered" from "the shell refused the registration". Everything
/// downstream of that — <c>Available</c> meaning provably available, the recovery episode, the deadline
/// — depends on this method returning the value Windows actually returned.
/// </para>
/// <para>
/// <b>CV-5.</b> The tooltip and the icon handle are resolved ONCE, at construction, and are then static
/// for the life of the registration. A re-registration after <c>TaskbarCreated</c> re-sends the same
/// bytes; it does not re-read the asset, re-localise the tooltip, or take any path that could fail for a
/// new reason at the worst moment. The single exception is a DPI change, which is an explicit,
/// externally triggered <c>NIM_MODIFY</c> and never part of a recovery episode.
/// </para>
/// </summary>
internal sealed class NativeTrayRegistration : INativeTrayRegistration, IDisposable
{
    private readonly nint _hwnd;
    private readonly string _tip;
    private readonly string _iconPath;
    private readonly ILogger _logger;

    internal const int MaxTooltipLength = 127;

    private nint _icon;
    private uint _iconDpi;
    private bool _disposed;

    /// <param name="hwnd">The tray host window. Must already exist: the callback target cannot be late.</param>
    /// <param name="iconPath">The .ico asset. Read once, here.</param>
    /// <param name="tip">The localized tooltip. Resolved once, here.</param>
    internal NativeTrayRegistration(nint hwnd, string iconPath, string tip, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(iconPath);

        if (hwnd == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hwnd), "The tray host window must exist first.");
        }

        if (!File.Exists(iconPath))
        {
            throw new FileNotFoundException("The Server Monitor tray icon asset is missing.", iconPath);
        }

        _hwnd = hwnd;
        _iconPath = iconPath;
        _logger = logger;

        _tip = FitTooltip(tip);

        _iconDpi = ResolveDpi(hwnd);
        _icon = LoadIconForDpi(_iconDpi);
    }

    public bool Add()
    {
        var data = Describe(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        var added = Shell_NotifyIconW(NIM_ADD, ref data);

        if (!added)
        {
            // Logged, never swallowed into a success: the caller's whole contract is this boolean.
            _logger.LogWarning(
                "Shell_NotifyIcon(NIM_ADD) was refused by the shell (last error {Error}).",
                Marshal.GetLastWin32Error());
        }

        return added;
    }

    public bool SetVersion()
    {
        var data = Describe(0);
        data.uVersion = NOTIFYICON_VERSION_4;

        var set = Shell_NotifyIconW(NIM_SETVERSION, ref data);

        if (!set)
        {
            // No v3 fallback. Under v3 the callback parameters mean something else entirely, so the
            // CV-1 contract would be validating fields that are not there. Failing here is correct.
            _logger.LogWarning(
                "Shell_NotifyIcon(NIM_SETVERSION, v4) failed (last error {Error}); the registration is not usable.",
                Marshal.GetLastWin32Error());
        }

        return set;
    }

    public bool Delete()
    {
        var data = Describe(0);
        var deleted = Shell_NotifyIconW(NIM_DELETE, ref data);

        if (!deleted)
        {
            // CV-16: an unverifiable cleanup is what escalates to the fail-safe exit. It must not be
            // reported as done.
            _logger.LogWarning(
                "Shell_NotifyIcon(NIM_DELETE) failed (last error {Error}); the cleanup is unverified.",
                Marshal.GetLastWin32Error());
        }

        return deleted;
    }

    /// <summary>
    /// Re-renders the icon for a new DPI. Called only from an explicit <c>WM_DPICHANGED</c>, never from
    /// a recovery episode.
    /// </summary>
    /// <remarks>
    /// The old handle is destroyed only AFTER <c>NIM_MODIFY</c> has returned, because the shell reads the
    /// handle during the call. Freeing first is the classic way to hand Explorer a dead HICON.
    /// </remarks>
    internal void UpdateForDpi(uint dpi)
    {
        if (_disposed || dpi == _iconDpi)
        {
            return;
        }

        var replacement = LoadIconForDpi(dpi);
        if (replacement == 0)
        {
            _logger.LogWarning("The tray icon could not be reloaded for {Dpi} DPI; keeping the current one.", dpi);
            return;
        }

        var previous = _icon;
        _icon = replacement;
        _iconDpi = dpi;

        var data = Describe(NIF_ICON);
        if (!Shell_NotifyIconW(NIM_MODIFY, ref data))
        {
            _logger.LogDebug("Shell_NotifyIcon(NIM_MODIFY) for the DPI change failed; the icon may be stale.");
        }

        // AFTER the call, never before.
        if (previous != 0)
        {
            DestroyIcon(previous);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Deliberately no NIM_DELETE here. Deleting the registration is a STATE MACHINE decision whose
        // result has to be observed and, when it fails, escalated. A silent delete on Dispose would be a
        // second authority over the same native object.
        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }
    }

    private NOTIFYICONDATAW Describe(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = TrayCallbackContract.IconId,
        uFlags = flags,
        uCallbackMessage = TrayCallbackContract.CallbackMessage,
        hIcon = _icon,
        szTip = _tip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private nint LoadIconForDpi(uint dpi)
    {
        var width = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
        var height = GetSystemMetricsForDpi(SM_CYSMICON, dpi);

        if (width <= 0 || height <= 0)
        {
            // Only as a last resort, and logged: a fixed 16x16 is the bug this avoids, not the default.
            _logger.LogWarning("GetSystemMetricsForDpi returned {Width}x{Height}; falling back to 16x16.", width, height);
            width = height = 16;
        }

        var icon = LoadImageW(0, _iconPath, IMAGE_ICON, width, height, LR_LOADFROMFILE | LR_DEFAULTCOLOR);

        if (icon == 0)
        {
            throw new InvalidOperationException(
                $"The tray icon could not be loaded at {width}x{height} (last error {Marshal.GetLastWin32Error()}).");
        }

        return icon;
    }

    /// <summary>
    /// Fits a tooltip into <c>NOTIFYICONDATAW.szTip</c>, which is a 128-character buffer INCLUDING the
    /// terminator. Truncating once, here, is preferable to a marshalling failure at <c>NIM_ADD</c> time —
    /// a localized string that happens to be long in one language must not turn into a tray that never
    /// registers in that language.
    /// </summary>
    internal static string FitTooltip(string? tip)
    {
        if (string.IsNullOrEmpty(tip))
        {
            return string.Empty;
        }

        return tip.Length > MaxTooltipLength ? tip[..MaxTooltipLength] : tip;
    }

    private static uint ResolveDpi(nint hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? USER_DEFAULT_SCREEN_DPI : dpi;
    }
}
