using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ServerMonitor.App.Services;

/// <summary>
/// Windows App SDK boundary for local notifications. Policy and content selection live in
/// ServerAlertCoordinator; this service only registers, displays and activates the app.
/// </summary>
public sealed class WindowsAppNotificationService : IUserNotificationService, IHostedService
{
    private const string ApplicationDisplayName = "Server Monitor";

    private readonly IWindowsAppNotificationPlatform _platform;
    private readonly IApplicationWindowController _windowController;
    private readonly ILogger<WindowsAppNotificationService> _logger;
    private readonly string _notificationIconPath;
    private readonly object _sync = new();
    private bool _registered;
    private bool _accepting;
    private bool _stopping;

    public WindowsAppNotificationService(
        IApplicationWindowController windowController,
        ILogger<WindowsAppNotificationService> logger)
        : this(
            new WindowsAppNotificationPlatform(),
            windowController,
            logger,
            Path.Combine(AppContext.BaseDirectory, "Assets", "ServerMonitorNotification.png"))
    {
    }

    internal WindowsAppNotificationService(
        IWindowsAppNotificationPlatform platform,
        IApplicationWindowController windowController,
        ILogger<WindowsAppNotificationService> logger,
        string notificationIconPath)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _windowController = windowController ?? throw new ArgumentNullException(nameof(windowController));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notificationIconPath = string.IsNullOrWhiteSpace(notificationIconPath)
            ? throw new ArgumentException("A notification icon path is required.", nameof(notificationIconPath))
            : notificationIconPath;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_registered || _stopping)
            {
                return Task.CompletedTask;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!_platform.IsSupported())
                {
                    _logger.LogWarning("Windows app notifications are unavailable on this system.");
                    return Task.CompletedTask;
                }

                if (!File.Exists(_notificationIconPath))
                {
                    _logger.LogWarning("Windows app notification registration skipped because its icon asset is missing.");
                    return Task.CompletedTask;
                }

                // Microsoft requires the activation handler to be connected before Register;
                // otherwise clicking a notification can launch an unnecessary second process.
                _platform.Invoked += OnNotificationInvoked;
                try
                {
                    _platform.Register(ApplicationDisplayName, new Uri(_notificationIconPath));
                    _registered = true;
                    _accepting = true;
                }
                catch
                {
                    _platform.Invoked -= OnNotificationInvoked;
                    throw;
                }

                _logger.LogInformation("Windows app notification service started.");
            }
            catch (Exception exception)
            {
                // OS policy, an unavailable Singleton package, or registration restrictions
                // must not prevent monitoring or the main window from starting.
                _logger.LogWarning(
                    exception,
                    "Windows app notifications could not be registered; monitoring will continue.");
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_stopping)
            {
                return Task.CompletedTask;
            }

            _stopping = true;
            _accepting = false;
            if (!_registered)
            {
                return Task.CompletedTask;
            }

            _registered = false;
            _platform.Invoked -= OnNotificationInvoked;
            try
            {
                _platform.Unregister();
                _logger.LogInformation("Windows app notification service stopped.");
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Windows app notification cleanup failed.");
            }
        }

        return Task.CompletedTask;
    }

    public Task ShowAsync(UserNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_registered || !_accepting || _stopping)
            {
                return Task.CompletedTask;
            }

            try
            {
                if (_platform.Setting != AppNotificationSetting.Enabled)
                {
                    _logger.LogDebug("Windows suppressed an app notification because its OS setting is disabled.");
                    return Task.CompletedTask;
                }

                _platform.Show(notification.Title, notification.Body);
                _logger.LogDebug(
                    "Windows app notification sent for {ServerId} ({Category}).",
                    notification.ServerId,
                    notification.Category);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Windows could not display an app notification for {ServerId}.",
                    notification.ServerId);
            }
        }

        return Task.CompletedTask;
    }

    private void OnNotificationInvoked(object? sender, EventArgs args)
    {
        lock (_sync)
        {
            if (!_registered || !_accepting || _stopping)
            {
                return;
            }
        }

        _windowController.RestoreAndActivate();
    }

    public void BeginShutdown()
    {
        lock (_sync)
        {
            _accepting = false;
        }
    }
}

internal interface IWindowsAppNotificationPlatform
{
    event EventHandler? Invoked;

    bool IsSupported();

    AppNotificationSetting Setting { get; }

    void Register(string displayName, Uri iconUri);

    void Unregister();

    void Show(string title, string body);
}

internal sealed class WindowsAppNotificationPlatform : IWindowsAppNotificationPlatform
{
    private readonly Func<bool> _isSupported;
    private readonly Func<AppNotificationManager> _managerFactory;
    private AppNotificationManager? _manager;
    private bool _handlerAttached;

    public WindowsAppNotificationPlatform()
        : this(AppNotificationManager.IsSupported, () => AppNotificationManager.Default)
    {
    }

    internal WindowsAppNotificationPlatform(
        Func<bool> isSupported,
        Func<AppNotificationManager> managerFactory)
    {
        _isSupported = isSupported ?? throw new ArgumentNullException(nameof(isSupported));
        _managerFactory = managerFactory ?? throw new ArgumentNullException(nameof(managerFactory));
    }

    public event EventHandler? Invoked;

    public AppNotificationSetting Setting => GetRegisteredManager().Setting;

    public bool IsSupported() => _isSupported();

    public void Register(string displayName, Uri iconUri)
    {
        // AppNotificationManager.Default may require the Windows App SDK Singleton package
        // for an unpackaged self-contained deployment. Do not resolve it before the caller
        // has passed IsSupported(); registration failures remain inside the service's
        // fail-soft boundary instead of escaping during dependency injection construction.
        var manager = _manager ??= _managerFactory();
        if (!_handlerAttached)
        {
            manager.NotificationInvoked += OnNotificationInvoked;
            _handlerAttached = true;
        }

        try
        {
            manager.Register(displayName, iconUri);
        }
        catch
        {
            manager.NotificationInvoked -= OnNotificationInvoked;
            _handlerAttached = false;
            throw;
        }
    }

    public void Unregister()
    {
        var manager = _manager;
        if (manager is null)
        {
            return;
        }

        try
        {
            manager.Unregister();
        }
        finally
        {
            if (_handlerAttached)
            {
                manager.NotificationInvoked -= OnNotificationInvoked;
                _handlerAttached = false;
            }
        }
    }

    public void Show(string title, string body)
    {
        var notification = new AppNotificationBuilder()
            .AddText(title)
            .AddText(body)
            .BuildNotification();
        GetRegisteredManager().Show(notification);
    }

    private AppNotificationManager GetRegisteredManager() =>
        _manager ?? throw new InvalidOperationException(
            "Windows app notifications have not been registered.");

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args) => Invoked?.Invoke(this, EventArgs.Empty);
}
