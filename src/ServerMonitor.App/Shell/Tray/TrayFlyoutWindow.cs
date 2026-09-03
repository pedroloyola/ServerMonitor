using System.Drawing;
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
internal sealed class TrayFlyoutWindow : IDisposable
{
    private readonly IThemeService _themeService;
    private readonly ILocalizationService _localization;
    private readonly ILogger _logger;

    private readonly Window _window;
    private readonly Grid _root;

    private MenuFlyout? _menu;
    private bool _disposed;

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
        if (_disposed)
        {
            return;
        }

        try
        {
            MoveTo(anchor);
            _window.Activate();

            var menu = BuildMenu();
            _menu = menu;
            menu.ShowAt(_root, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Auto });
        }
        catch (Exception exception)
        {
            // A flyout that cannot open must not take the process with it, and must not leave the gate
            // held — the caller learns through Closed that the slot is free again.
            _logger.LogError(exception, "The tray flyout could not be shown.");
            OnClosed(null, null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _themeService.Detach(_root);
            _menu?.Hide();
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

    private MenuFlyoutItem CreateItem(TrayCommand command)
    {
        var text = _localization.GetString(TrayFlyoutMenu.ResourceKeyFor(command));
        var item = new MenuFlyoutItem { Text = text };
        AutomationProperties.SetName(item, text);
        item.Click += (_, _) => CommandInvoked?.Invoke(this, command);
        return item;
    }

    private void OnClosed(object? sender, object? args)
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

        Closed?.Invoke(this, EventArgs.Empty);
    }
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
