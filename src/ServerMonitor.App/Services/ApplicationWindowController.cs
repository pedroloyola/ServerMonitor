using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Windowing;
using WinUIEx;

namespace ServerMonitor.App.Services;

/// <summary>
/// The only implementation of <see cref="IWindowHideCapability"/>, and deliberately a SEPARATE object from
/// the window controller.
/// </summary>
/// <remarks>
/// If the controller implemented the capability itself, every holder of the controller could reach it with
/// a cast, and the whole point is that holding the window contract must not be enough. Nothing on the
/// controller returns one of these, so the only way to have one is to be handed it by the composition
/// root — which hands it to exactly two consumers.
/// </remarks>
internal sealed class WindowHideCapability(ApplicationWindowController controller) : IWindowHideCapability
{
    public void HideToBackground() => controller.HideToBackgroundCore();

    public void HideForMinimize() => controller.HideForMinimizeCore();
}

public sealed class ApplicationWindowController(
    INavigationService navigationService,
    IWindowModeCoordinator modeCoordinator,
    ILogger<ApplicationWindowController> logger) : IApplicationWindowController
{
    private readonly object _sync = new();
    private Window? _window;
    private AppWindow? _appWindow;
    private DispatcherQueue? _dispatcherQueue;
    private Func<Window>? _windowFactory;
    private int _shutdownStarted;

    public bool IsAttached
    {
        get { lock (_sync) { return _window is not null; } }
    }

    public bool IsMaterialized => IsAttached;

    /// <summary>
    /// A headless launch has no window and no UI dispatcher of its own to enqueue onto, so the factory
    /// is stored with the dispatcher that owns it: whoever registers the factory is on the UI thread.
    /// </summary>
    public void AttachWindowFactory(Func<Window> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_sync)
        {
            _windowFactory = factory;
            _dispatcherQueue ??= DispatcherQueue.GetForCurrentThread();
        }
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

    internal void HideForMinimizeCore() => RunOnUiThread(() =>
    {
        if (_appWindow is null)
        {
            return;
        }

        _appWindow.IsShownInSwitchers = false;
        _appWindow.Hide();
        logger.LogDebug("Main window hidden after minimize.");
    });

    /// <summary>
    /// The BACKGROUND transition. Identical window mechanics to the minimize path; a headless process
    /// with no window is already in the target state, so this is a no-op there rather than an error.
    /// </summary>
    internal void HideToBackgroundCore() => RunOnUiThread(() =>
    {
        if (_appWindow is null)
        {
            return;
        }

        _appWindow.IsShownInSwitchers = false;
        _appWindow.Hide();
        logger.LogDebug("Main window hidden to background.");
    });

    public void RestoreAndActivate() => RunOnUiThread(() =>
    {
        if (!TryMaterialize())
        {
            return;
        }

        _appWindow!.IsShownInSwitchers = true;
        _appWindow.Show();
        WindowManager.Get(_window!).WindowState = WindowState.Normal;
        _window!.Activate();
        _window.SetForegroundWindow();
        logger.LogDebug("Main window restored from the system tray.");
    });

    public void OpenSettings() => RunOnUiThread(() =>
    {
        if (!TryMaterialize())
        {
            return;
        }

        _appWindow!.IsShownInSwitchers = true;
        _appWindow.Show();
        WindowManager.Get(_window!).WindowState = WindowState.Normal;
        _window!.Activate();
        _window.SetForegroundWindow();
        navigationService.GoToSettings();
    });

    /// <summary>
    /// The background notice's activation target. Order is deliberate and is the whole point: materialize
    /// if needed, NAVIGATE while the window is still hidden, and only then show and activate. Showing
    /// first and navigating after (what <see cref="OpenSettings"/> does, safely, because it is invoked
    /// from an already-visible window) would present the Dashboard for a frame — and this notice exists
    /// precisely because the user just asked for the Dashboard to go away.
    /// </summary>
    public void OpenBackgroundSettings() => RunOnUiThread(() =>
    {
        if (!TryMaterialize())
        {
            return;
        }

        navigationService.GoToSettings();
        navigationService.RequestBackgroundSettingsFocus();

        _appWindow!.IsShownInSwitchers = true;
        _appWindow.Show();
        WindowManager.Get(_window!).WindowState = WindowState.Normal;
        _window!.Activate();
        _window.SetForegroundWindow();
        logger.LogDebug("Opened Settings on the background section from a notification.");
    });

    /// <summary>
    /// Ensures a window exists, creating it from the registered factory on first use. Returns false only
    /// when there is neither a window nor a way to make one, which is the pre-shell startup window.
    /// Must be called on the UI thread (every caller runs inside <see cref="RunOnUiThread"/>).
    /// </summary>
    private bool TryMaterialize()
    {
        if (_window is not null && _appWindow is not null)
        {
            return true;
        }

        Func<Window>? factory;
        lock (_sync)
        {
            factory = _windowFactory;
        }

        if (factory is null)
        {
            logger.LogDebug("Window command ignored because no window exists and none can be created.");
            return false;
        }

        try
        {
            Attach(factory());
            logger.LogInformation("Main window materialized on demand.");
            return _window is not null && _appWindow is not null;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The main window could not be materialized.");
            return false;
        }
    }

    public void ToggleCompactMode() => RunOnUiThread(() =>
    {
        if (!TryMaterialize())
        {
            return;
        }

        // Toggling from the tray must also bring the window back from the tray first, then switch,
        // so the mode change lands on a visible, activated window in a consistent state.
        _appWindow!.IsShownInSwitchers = true;
        _appWindow.Show();
        WindowManager.Get(_window!).WindowState = WindowState.Normal;
        _window!.Activate();
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
