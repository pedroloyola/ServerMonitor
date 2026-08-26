using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Windowing;
using WinUIEx;

namespace ServerMonitor.App.Services;

public sealed class ApplicationWindowController(
    INavigationService navigationService,
    IWindowModeCoordinator modeCoordinator,
    ILogger<ApplicationWindowController> logger) : IApplicationWindowController
{
    private readonly object _sync = new();
    private Window? _window;
    private AppWindow? _appWindow;
    private DispatcherQueue? _dispatcherQueue;
    private int _shutdownStarted;

    public bool IsAttached
    {
        get { lock (_sync) { return _window is not null; } }
    }

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (_sync)
        {
            if (_window is not null && !ReferenceEquals(_window, window))
            {
                throw new InvalidOperationException("The application window controller is already attached.");
            }

            _window = window;
            _appWindow = window.AppWindow;
            _dispatcherQueue = window.DispatcherQueue;
        }
    }

    public void HideForMinimize() => RunOnUiThread(() =>
    {
        if (_appWindow is null)
        {
            return;
        }

        _appWindow.IsShownInSwitchers = false;
        _appWindow.Hide();
        logger.LogDebug("Main window hidden after minimize.");
    });

    public void RestoreAndActivate() => RunOnUiThread(() =>
    {
        if (_window is null || _appWindow is null)
        {
            return;
        }

        _appWindow.IsShownInSwitchers = true;
        _appWindow.Show();
        WindowManager.Get(_window).WindowState = WindowState.Normal;
        _window.Activate();
        _window.SetForegroundWindow();
        logger.LogDebug("Main window restored from the system tray.");
    });

    public void OpenSettings() => RunOnUiThread(() =>
    {
        if (_window is null || _appWindow is null)
        {
            return;
        }

        _appWindow.IsShownInSwitchers = true;
        _appWindow.Show();
        WindowManager.Get(_window).WindowState = WindowState.Normal;
        _window.Activate();
        _window.SetForegroundWindow();
        navigationService.GoToSettings();
    });

    public void ToggleCompactMode() => RunOnUiThread(() =>
    {
        if (_window is null || _appWindow is null)
        {
            return;
        }

        // Toggling from the tray must also bring the window back from the tray first, then switch,
        // so the mode change lands on a visible, activated window in a consistent state.
        _appWindow.IsShownInSwitchers = true;
        _appWindow.Show();
        WindowManager.Get(_window).WindowState = WindowState.Normal;
        _window.Activate();
        _window.SetForegroundWindow();
        modeCoordinator.Toggle();
        logger.LogDebug("Toggled compact mode from the system tray.");
    });

    public void RequestClose() => RunOnUiThread(() => _window?.Close());

    public void BeginShutdown() => Interlocked.Exchange(ref _shutdownStarted, 1);

    private void RunOnUiThread(Action action)
    {
        if (Volatile.Read(ref _shutdownStarted) != 0)
        {
            return;
        }

        DispatcherQueue? dispatcher;
        lock (_sync)
        {
            dispatcher = _dispatcherQueue;
        }

        if (dispatcher is null)
        {
            logger.LogDebug("Window command ignored because the main window is not attached yet.");
            return;
        }

        if (dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        if (!dispatcher.TryEnqueue(() =>
            {
                if (Volatile.Read(ref _shutdownStarted) == 0)
                {
                    action();
                }
            }))
        {
            logger.LogDebug("Window command ignored because the UI dispatcher is unavailable.");
        }
    }
}
