using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ServerMonitor.App.Services;
using Windows.Graphics;

namespace ServerMonitor.App.Shell.Tray;

/// <summary>The five tray commands, in the order the product fixed. Closed list.</summary>
internal enum TrayCommand
{
    Open,
    ToggleCompact,
    RefreshAll,
    Settings,
    Exit
}

/// <summary>
/// The tray flyout host: Prism's OPTION A, a minimal XAML window that exists only to give a
/// <see cref="MenuFlyout"/> somewhere to live.
/// <para>
/// It is never the app's window in any sense a user can observe: it is excluded from the taskbar and
/// Alt-Tab, has no title bar or border, is one pixel, and is moved to the anchor and shown only for as
/// long as the menu is open. It is a XAML root, which is exactly why it must be attached to
/// <see cref="IThemeService"/> — <c>RequestedTheme</c> is per-root, so without that the menu would
/// render in the system theme while the Dashboard rendered in the chosen one.
/// </para>
/// <para>
/// <b>It decides nothing.</b> Whether a request is allowed to open a flyout at all is
/// <see cref="FlyoutReentrancyGate"/>'s answer (CV-9), and whether the message was trustworthy is
/// <see cref="TrayCallbackContract"/>'s. This type shows a menu and reports which item was clicked.
/// </para>
/// </summary>
internal sealed class TrayFlyoutWindow : IFlyoutSurface, IDisposable
{
    private readonly IThemeService _themeService;
    private readonly ILocalizationService _localization;
    private readonly ILogger _logger;

    private readonly Window _window;
    private readonly Grid _root;

    private MenuFlyout? _menu;
    private bool _disposed;

    /// <summary>The foreground-change hook that observes the dismissal.</summary>
    private nint _foregroundHook;

    /// <summary>
    /// Held for as long as the hook might call back. Dropping it while the hook is still installed would
    /// leave native code calling into collected memory, which is why it is only cleared once
    /// <c>UnhookWinEvent</c> has actually reported success.
    /// </summary>
    private WinEventDelegate? _foregroundCallback;

    private FlyoutLifecycle? _lifecycle;

    private FlyoutLifecycle Lifecycle =>
        _lifecycle ??= new FlyoutLifecycle(this, () => Closed?.Invoke(this, EventArgs.Empty));

    /// <summary>Raised with the command the user picked. Never raised for a dismissal.</summary>
    internal event EventHandler<TrayCommand>? CommandInvoked;

    /// <summary>Raised once the menu is gone, whether it was used or dismissed.</summary>
    internal event EventHandler? Closed;

    internal TrayFlyoutWindow(IThemeService themeService, ILocalizationService localization, ILogger logger)
    {
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _root = new Grid { Width = 1, Height = 1 };
        _window = new Window { Content = _root };


        ConfigurePresentation();

        // The second XAML root. Attaching it is the whole Prism HIGH.
        _themeService.Attach(_root);
    }

    /// <summary>
    /// Shows the menu at <paramref name="anchor"/>.
    /// </summary>
    /// <remarks>
    /// The caller must already hold the <see cref="FlyoutReentrancyGate"/>. This method does not check
    /// reentrancy itself, because a second authority over the same question is how the first one stops
    /// being the answer.
    /// </remarks>
    internal void Show(Point anchor)
    {
        _root.Loaded -= OnRootLoaded;
        _root.Loaded += OnRootLoaded;

        try
        {
            Lifecycle.Show(anchor);
        }
        catch (Exception exception)
        {
            // The lifecycle has already released the slot on its way out; this only keeps a failure to
            // present from unwinding into the caller.
            _logger.LogError(exception, "The tray flyout could not be shown.");
        }
    }

    private void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        _root.Loaded -= OnRootLoaded;

        try
        {
            Lifecycle.OnSurfaceReady();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The tray flyout could not be shown once its root had loaded.");
        }
    }

    // ---------------------------------------------------------------- IFlyoutSurface

    /// <summary>
    /// The condition the PLATFORM demands, and nothing stricter. The failure was literally "this element
    /// does not have a XamlRoot"; requiring <c>IsLoaded</c> as well was my own addition, and after the
    /// window is hidden it stays false while the XamlRoot is present — which blocked every later request.
    /// Measured, twice, before this line was narrowed.
    /// </summary>
    bool IFlyoutSurface.IsPresentable => !_disposed && _root.XamlRoot is not null;

    void IFlyoutSurface.MoveTo(Point anchor) => MoveTo(anchor);

    void IFlyoutSurface.Activate()
    {
        // SHOW, then activate. HideWindow() hides the AppWindow between requests, and a hidden window's
        // XAML root unloads -- XamlRoot goes null and Loaded will not fire again until it is shown. So a
        // later request found IsPresentable false and waited for a signal that could never arrive: the
        // same liveness shape as the original defect, one layer in. Measured, not reasoned.
        //
        // Shown WITHOUT activation so nothing is taken from the user; the Activate() below is the same
        // call the design already made.
        _window.Activate();
    }

    void IFlyoutSurface.PresentMenu()
    {
        var menu = BuildMenu();
        _menu = menu;
        menu.ShowAt(_root, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Auto });
    }

    bool IFlyoutSurface.TryHideMenu()
    {
        var menu = _menu;
        if (menu is null)
        {
            return false;
        }

        try
        {
            menu.Hide();
            return true;
        }
        catch (Exception exception)
        {
            // A hide that failed produces no Closed, so the caller has to treat this as terminal.
            _logger.LogDebug(exception, "The tray flyout menu could not be hidden.");
            return false;
        }
    }

    void IFlyoutSurface.HideWindow()
    {
        _menu = null;

        try
        {
            // Hidden, not closed: the window is reused, and recreating a XAML root per right-click would
            // re-enter the theme attachment on every click.
            _window.AppWindow.Hide();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "The tray flyout window could not be hidden.");
        }
    }

    /// <summary>
    /// The dismissal source of truth, and it is deliberately NOT a window event.
    /// </summary>
    /// <remarks>
    /// Measured: when this process does not own the foreground, dismissing by clicking elsewhere produces
    /// NO <c>MenuFlyout.Closed</c>, no <c>Closing</c>, and no <c>Activated(Deactivated)</c> — the window
    /// was never truly foreground, so Windows never deactivates it. Every in-process signal is absent, so
    /// the menu stayed open forever and the gate was never released.
    /// <para>
    /// A foreground CHANGE is observable without owning the foreground and without taking it. When it
    /// moves to anything that is not this window, the menu is closed here, which produces the ordinary
    /// <c>Closed</c> the rest of the design already depends on. No focus is taken, nothing is made
    /// topmost, and the gate keeps its single owner.
    /// </para>
    /// </remarks>
    bool IFlyoutSurface.TryInstallDismissalWatch()
    {
        if (_foregroundHook != 0)
        {
            return true;
        }

        var callback = new WinEventDelegate(OnForegroundChanged);
        var hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            nint.Zero,
            callback,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        if (hook == 0)
        {
            // DOCUMENTED FAILURE, AND NOT IGNORED. Zero means no callback will ever arrive — and this is
            // the dismissal signal in exactly the states where MenuFlyout.Closed was measured absent. A
            // menu shown here could never be dismissed and would hold the slot for the session, so the
            // request fails closed instead.
            _logger.LogError("The tray flyout could not install its dismissal watch; the menu is not shown.");
            return false;
        }

        _foregroundCallback = callback;
        _foregroundHook = hook;
        return true;
    }

    void IFlyoutSurface.RemoveDismissalWatch()
    {
        if (_foregroundHook == 0)
        {
            return;
        }

        if (UnhookWinEvent(_foregroundHook))
        {
            _foregroundHook = 0;
            _foregroundCallback = null;
            return;
        }

        // The unhook FAILED, so the hook may still fire. The handle is forgotten -- there is nothing more
        // to do with it -- but the delegate is DELIBERATELY KEPT ALIVE, because releasing it while native
        // code can still call it is a use-after-free rather than a leak.
        _logger.LogWarning("The tray flyout could not remove its dismissal watch; the callback is retained.");
        _foregroundHook = 0;
    }

    /// <summary>
    /// The native callback. NOTHING may unwind from here: a managed exception crossing back into the
    /// window-event dispatcher is undefined, and it would also strand the slot.
    /// </summary>
    private void OnForegroundChanged(
        nint hook, uint eventId, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            if (hwnd == nint.Zero || _disposed)
            {
                return;
            }

            // OUR PROCESS, not our one window. The menu is hosted in its own top-level popup window, so
            // showing it changes the foreground to a handle that is not _window -- and comparing against
            // that single handle made the flyout dismiss ITSELF the moment it appeared. Measured: with the
            // watch installed before the menu is shown, request 3 opened and vanished.
            _ = GetWindowThreadProcessId(hwnd, out var owningProcess);
            if (owningProcess == CurrentProcessId)
            {
                return;
            }

            Lifecycle.OnForeignForeground();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The tray flyout failed to dismiss on a foreground change.");

            // Even here the slot is freed: an unusable menu must not outlive the request that opened it.
            try
            {
                Lifecycle.Dispose();
            }
            catch (Exception disposeFailure)
            {
                _logger.LogDebug(disposeFailure, "The tray flyout lifecycle could not be terminated.");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The slot is freed FIRST, and unconditionally. The previous version relied on the menu's own
        // close to do it -- and the report claimed this method released it, which was simply untrue.
        try
        {
            Lifecycle.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "The tray flyout lifecycle could not be terminated on dispose.");
        }

        try
        {
            _root.Loaded -= OnRootLoaded;
            _themeService.Detach(_root);
            _window.Close();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "The tray flyout window could not be closed cleanly.");
        }
    }

    private void ConfigurePresentation()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(handle));

        // Out of the taskbar and out of Alt-Tab: a one-pixel helper window that shows up in the switcher
        // is a bug the user sees long before they see the menu.
        appWindow.IsShownInSwitchers = false;

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            // Deliberately NOT always-on-top. The window is activated when the menu opens, so the menu
            // is already in front, and the topmost flag has a single owner in this codebase
            // (AppWindowPlacementAdapter, M9 compact mode). Taking a second writer for no gain is how a
            // boundary stops being one.
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        appWindow.Resize(new SizeInt32(1, 1));
    }

    private void MoveTo(Point anchor)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(handle));
        appWindow.Move(new PointInt32(anchor.X, anchor.Y));
    }

    /// <summary>
    /// Builds the menu fresh each time. The order is product-fixed and is asserted by
    /// <c>TrayFlyoutMenuTests</c> against <see cref="TrayFlyoutMenu.Order"/>, so it cannot drift here
    /// without failing there.
    /// </summary>
    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();

        foreach (var command in TrayFlyoutMenu.Order)
        {
            if (command == TrayCommand.Exit)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            menu.Items.Add(CreateItem(command));
        }

        menu.Closed += OnClosed;
        return menu;
    }

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private delegate void WinEventDelegate(
        nint hook, uint eventId, nint hwnd, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint module,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    private static uint CurrentProcessId { get; } =
        (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

    private MenuFlyoutItem CreateItem(TrayCommand command)
    {
        var text = _localization.GetString(TrayFlyoutMenu.ResourceKeyFor(command));
        var item = new MenuFlyoutItem { Text = text };
        AutomationProperties.SetName(item, text);
        item.Click += (_, _) => CommandInvoked?.Invoke(this, command);
        return item;
    }

    private void OnClosed(object? sender, object? args) => Lifecycle.OnMenuClosed();
}

/// <summary>
/// The menu, as data. Separated from the window so the order — which is product-fixed and
/// non-negotiable — is assertable without a desktop.
/// </summary>
internal static class TrayFlyoutMenu
{
    /// <summary>
    /// Abrir o ServerAlyzer · Modo compacto · Atualizar todos · Definições · Sair do ServerAlyzer.
    /// </summary>
    internal static readonly TrayCommand[] Order =
    [
        TrayCommand.Open,
        TrayCommand.ToggleCompact,
        TrayCommand.RefreshAll,
        TrayCommand.Settings,
        TrayCommand.Exit
    ];

    internal static string ResourceKeyFor(TrayCommand command) => command switch
    {
        TrayCommand.Open => "TrayOpenMenuItem",
        TrayCommand.ToggleCompact => "TrayCompactModeMenuItem",
        TrayCommand.RefreshAll => "TrayRefreshAllMenuItem",
        TrayCommand.Settings => "TraySettingsMenuItem",
        TrayCommand.Exit => "TrayExitMenuItem"
        // No `_ =>` arm: CS8509 is an error in this project, so a new command cannot be added without
        // deciding what it is called.
    };
}
