using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using ServerMonitor.ActivationContract;
using ServerMonitor.App.Services;

namespace ServerMonitor.App;

/// <summary>
/// Custom entry point (replaces the XAML-generated Main via DISABLE_XAML_GENERATED_MAIN) so the
/// single-instance decision runs as early as possible — before any window, DI host, tray, SQLite
/// writer or MonitoringEngine is created (M12/ADR-017 §6, §89). A redirected second launch never
/// builds the app; it forwards its activation to the running instance and exits.
/// </summary>
public static class Program
{
    private static IntPtr _redirectEventHandle = IntPtr.Zero;

    // The single-instance key registered by the primary instance (null when bypassed for Debug QA).
    // Released on shutdown so a launch that races the primary's exit becomes the new primary instead
    // of redirecting into a process that is tearing down (M-1/shutdown race, Atlas reliability review).
    private static string? _registeredInstanceKey;

    /// <summary>
    /// Releases the single-instance registration so a subsequent launch can take over cleanly during
    /// shutdown. Best-effort and idempotent; the OS releases the key on process exit regardless.
    /// </summary>
    public static void ReleaseSingleInstanceKey()
    {
        var key = Interlocked.Exchange(ref _registeredInstanceKey, null);
        if (key is null)
        {
            return;
        }

        try
        {
            AppInstance.GetCurrent().UnregisterKey();
        }
        catch
        {
            // Best-effort: process exit releases the key anyway.
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (ShouldRedirectToExistingInstance())
        {
            // Activation was forwarded to the already-running instance; exit this process.
            return 0;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    // Returns true when this launch should redirect to an existing instance and exit.
    private static bool ShouldRedirectToExistingInstance()
    {
        var isDebugBuild =
#if DEBUG
            true;
#else
            false;
#endif
        var key = SingleInstancePolicy.ResolveInstanceKey(Environment.GetCommandLineArgs(), isDebugBuild);
        if (key is null)
        {
            // Bypass: allow multiple instances (Debug QA harnesses only). Still funnel THIS launch's own
            // cold activation intent so a serveralyzer:// Debug launch routes through the same hand-off.
            _pendingActivation.Deliver(ProtocolActivationReader.TryGetIntent(
                AppInstance.GetCurrent().GetActivatedEventArgs()));
            return false;
        }

        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey(key);

        if (keyInstance.IsCurrent)
        {
            // We are the primary instance. Remember the key so it can be released on shutdown. Deliver THIS
            // launch's own cold intent BEFORE subscribing to redirects, so a later redirect (a newer user
            // action) correctly supersedes it under the hand-off's latest-wins rule (§M-1).
            _registeredInstanceKey = key;
            _pendingActivation.Deliver(ProtocolActivationReader.TryGetIntent(activationArgs));
            keyInstance.Activated += OnActivated;
            return false;
        }

        RedirectActivationTo(activationArgs, keyInstance);
        return true;
    }

    // The single hand-off for activation intents across the App-construction boundary (§M-1/§M-2). The
    // cold intent and every redirect are delivered here; the App attaches the router's Route once built.
    private static readonly PendingActivation _pendingActivation = new();

    /// <summary>
    /// Attaches the activation consumer (the router's Route) once the App has built it, atomically flushing
    /// the latest intent buffered before the App object existed.
    /// </summary>
    public static void AttachActivationConsumer(Action<ActivationIntent> consumer) =>
        _pendingActivation.Attach(consumer);

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        // A second launch (notification click, ExtendedActivationKind.AppNotification, or a
        // serveralyzer:// protocol/widget deep-link) was redirected here. Funnel any deep-link intent
        // through the single hand-off (buffered before the App exists, delivered straight to the router
        // after) and, if the shell is already up, foreground the one authoritative window. Routing never
        // reads Application.Current — it is set before the router is built, so it is not a readiness flag
        // (§M-1). A non-deep-link activation carries a null intent and only restores the window.
        _pendingActivation.Deliver(ProtocolActivationReader.TryGetIntent(args));
        (Application.Current as App)?.RestoreOnRedirect();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(
        IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint dwFlags, uint dwMilliseconds, ulong nHandles, IntPtr[] pHandles, out uint dwIndex);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // Redirects on another thread and uses a non-blocking wait so the STA message pump stays
    // responsive, then brings the running instance's window to the foreground (Microsoft pattern).
    private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
    {
        _redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            // Always signal the event, even if redirection throws (e.g. the primary exited
            // mid-redirect); otherwise the wait below would block INFINITE and hang this
            // second instance instead of exiting (M-1, Atlas reliability review).
            try
            {
                keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
            }
            finally
            {
                SetEvent(_redirectEventHandle);
            }
        });

        const uint CWMO_DEFAULT = 0;
        const uint INFINITE = 0xFFFFFFFF;
        _ = CoWaitForMultipleObjects(
            CWMO_DEFAULT, INFINITE, 1, [_redirectEventHandle], out _);

        try
        {
            using var process = Process.GetProcessById((int)keyInstance.ProcessId);
            var mainWindowHandle = process.MainWindowHandle;
            if (mainWindowHandle != IntPtr.Zero)
            {
                SetForegroundWindow(mainWindowHandle);
            }
        }
        catch (ArgumentException)
        {
            // The target instance exited between redirect and foreground; nothing to activate.
        }
        catch (InvalidOperationException)
        {
            // Same as above — the process object is no longer associated with a running process.
        }
    }
}
