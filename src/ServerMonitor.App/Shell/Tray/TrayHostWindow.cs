using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using static ServerMonitor.App.Shell.Tray.NativeTrayInterop;

namespace ServerMonitor.App.Shell.Tray;

/// <summary>
/// The window that owns the tray registration and receives its callbacks.
/// <para>
/// <b>Top-level and unowned, never shown, and deliberately NOT a message-only window.</b> S-1(A)
/// measured that <c>TaskbarCreated</c> is delivered to a top-level unowned window that is never shown,
/// in both the headless and the foreground packaged cases; it is NOT delivered to <c>HWND_MESSAGE</c>
/// children, because the shell broadcasts to top-level windows only. <c>WS_EX_TOOLWINDOW</c> keeps it
/// out of Alt-Tab and the taskbar without making it a child of anything.
/// </para>
/// <para>
/// The <c>WndProc</c> itself decides nothing. It decodes through <see cref="TrayCallbackContract"/> and
/// calls the state machine; the trust model lives in the contract, where it is testable without a
/// desktop.
/// </para>
/// </summary>
internal sealed class TrayHostWindow : IDisposable
{
    private const string WindowClassName = "ServerAlyzer.TrayHost";

    private readonly ILogger _logger;
    private readonly WndProcDelegate _wndProc;
    private readonly uint _taskbarCreated;

    private nint _hwnd;
    private bool _disposed;

    /// <summary>Raised for a decoded, validated callback. Never raised for a refused message.</summary>
    internal event EventHandler<TrayCallback>? CallbackReceived;

    /// <summary>Raised when the shell announces that the taskbar was recreated.</summary>
    internal event EventHandler? TaskbarCreated;

    /// <summary>Raised when the host window's DPI changes, carrying the new DPI.</summary>
    internal event EventHandler<uint>? DpiChanged;

    internal TrayHostWindow(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Held in a field for the window's whole life: letting the delegate be collected while Windows
        // still holds the thunk is the classic crash in this pattern.
        _wndProc = OnMessage;

        var instance = GetModuleHandleW(null);
        RegisterWindowClass(instance);

        _hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW,
            WindowClassName,
            WindowClassName,
            WS_OVERLAPPED,
            0, 0, 0, 0,
            hWndParent: 0,   // top-level and unowned. NOT HWND_MESSAGE.
            hMenu: 0,
            hInstance: instance,
            lpParam: 0);

        if (_hwnd == 0)
        {
            throw new InvalidOperationException(
                $"The tray host window could not be created (last error {Marshal.GetLastWin32Error()}).");
        }

        // Resolved at runtime, per session. A hard-coded id would be a different message elsewhere.
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");

        if (_taskbarCreated == 0)
        {
            // Not fatal: the icon still works, only automatic recovery after an Explorer restart is lost.
            _logger.LogWarning("RegisterWindowMessage(\"TaskbarCreated\") failed; tray recovery is unavailable.");
        }
    }

    /// <summary>The handle the registration must target.</summary>
    internal nint Handle => _hwnd;

    /// <summary>The DPI the host window currently reports.</summary>
    internal uint Dpi => _hwnd == 0 ? USER_DEFAULT_SCREEN_DPI : GetDpiForWindow(_hwnd);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != 0)
        {
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }

    private static void RegisterClass(ref WNDCLASSEXW description)
    {
        if (RegisterClassExW(ref description) != 0)
        {
            return;
        }

        const int ERROR_CLASS_ALREADY_EXISTS = 1410;
        var error = Marshal.GetLastWin32Error();

        // Registering the same class twice in one process is benign — the first registration stands.
        if (error != ERROR_CLASS_ALREADY_EXISTS)
        {
            throw new InvalidOperationException($"The tray host window class could not be registered (error {error}).");
        }
    }

    private void RegisterWindowClass(nint instance)
    {
        var description = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = Marshal.StringToHGlobalUni(WindowClassName)
        };

        try
        {
            RegisterClass(ref description);
        }
        finally
        {
            // The class name is copied by the system during registration.
            Marshal.FreeHGlobal(description.lpszClassName);
        }
    }

    private nint OnMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        // Default-deny by structure: every branch below is an explicit accept, and anything that reaches
        // the bottom goes to DefWindowProc without ever having produced an effect.
        if (msg == TrayCallbackContract.CallbackMessage)
        {
            var callback = TrayCallbackContract.TryDecode(msg, wParam, lParam, IsOnScreen);

            if (callback is { } decoded)
            {
                Raise(() => CallbackReceived?.Invoke(this, decoded), "tray callback");
            }

            return 0;
        }

        if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            // No payload is read. The message is a NOTIFICATION that the shell restarted, and the state
            // machine decides — with the frequency limiter — whether anything happens at all.
            Raise(() => TaskbarCreated?.Invoke(this, EventArgs.Empty), "taskbar recreation");
            return 0;
        }

        if (msg == WM_DPICHANGED)
        {
            var dpi = (uint)(wParam.ToInt64() & 0xFFFF);
            Raise(() => DpiChanged?.Invoke(this, dpi), "DPI change");
            return 0;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// A managed exception must never unwind through the native frame that called this WndProc: the
    /// behaviour there is undefined, and the failure mode is a process the user cannot diagnose.
    /// </summary>
    private void Raise(Action raise, string what)
    {
        try
        {
            raise();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The {What} handler threw; the message is dropped.", what);
        }
    }

    private static bool IsOnScreen(Point point) =>
        MonitorFromPoint(new POINT { x = point.X, y = point.Y }, MONITOR_DEFAULTTONULL) != 0;
}
