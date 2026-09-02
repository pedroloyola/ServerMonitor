using System;
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

    /// <summary>
    /// One redirected activation (a notification click, an <c>ExtendedActivationKind.AppNotification</c>,
    /// or a <c>serveralyzer://</c> protocol/widget deep-link) — dispatched so that it produces EXACTLY ONE
    /// restore of the window (M13-QA-10 defensive fix B); see <see cref="ActivationDispatch"/>. Routing
    /// never reads <c>Application.Current</c> as a readiness flag: it is set while the derived App
    /// constructor is still wiring the router (§M-1).
    /// </summary>
    private static void OnActivated(object? sender, AppActivationArguments args) =>
        _activationDispatch.Dispatch(ProtocolActivationReader.TryGetIntent(args));

    /// <summary>
    /// The redirect step: deliver the intent, and restore the window only when nothing else will. Built
    /// from the two fields above; the lambdas read them at call time, so declaration order is irrelevant.
    /// </summary>
    private static readonly ActivationDispatch _activationDispatch = new(
        intent => _pendingActivation.Deliver(intent),
        () => (Application.Current as App)?.RestoreOnRedirect());

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(
        IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint dwFlags, uint dwMilliseconds, ulong nHandles, IntPtr[] pHandles, out uint dwIndex);

    /// <summary>
    /// Redirects on another thread and uses a non-blocking wait so the STA message pump stays responsive.
    /// <para>
    /// It deliberately does NOT try to foreground the running instance itself (M13-QA-10 defensive fix A).
    /// It used to call <c>SetForegroundWindow(Process.GetProcessById(pid).MainWindowHandle)</c>, and that
    /// handle is a guess taken from OUTSIDE the target process: measured on the shipping build, the app
    /// has more than one top-level window of the same class, so the property returns the first VISIBLE
    /// unowned one — which is <c>IntPtr.Zero</c> whenever the window is minimized to the tray, and would
    /// be the hidden 1440x789 <c>TOPMOST</c> WinUIEx helper window if that one ever became visible first.
    /// Either way this process cannot know which HWND is authoritative; the primary can, because it owns
    /// the <c>MainWindow</c>/<c>AppWindow</c>, and it already surfaces itself through
    /// <see cref="Services.IApplicationWindowController.RestoreAndActivate"/> using that window's own
    /// handle. Removing the guess loses nothing: <c>RedirectActivationToAsync</c> is what carries the
    /// activation (and the foreground right) across, and the primary was measured coming to the
    /// foreground with this call skipped entirely — from a launcher with no foreground rights, with the
    /// window hidden in the tray, on a machine with the foreground lock fully engaged.
    /// </para>
    /// This is activation hygiene, NOT the QA-10 fix: a widget click is covered by the board, which is a
    /// <c>WS_EX_TOPMOST</c> window, and no amount of foreground work on our side changes that.
    /// </summary>
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

        // Nothing else to do here. Surfacing the window is the PRIMARY instance's job, from the one
        // authoritative handle it owns (see the remarks above); this process only forwards and exits.
    }
}
