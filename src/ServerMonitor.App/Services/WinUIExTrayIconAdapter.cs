using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinUIEx;

namespace ServerMonitor.App.Services;

/// <summary>
/// Owns exactly one WinUIEx tray icon. WinUIEx provides the Shell_NotifyIcon lifecycle,
/// DPI-aware SVG rendering, and TaskbarCreated re-registration without app-side polling.
/// </summary>
public sealed class WinUIExTrayIconAdapter(
    ILocalizationService localizationService,
    ILogger<WinUIExTrayIconAdapter> logger) : ITrayIconAdapter
{
    internal const uint StableTrayIconId = 1;
    private TrayIcon? _trayIcon;
    private DispatcherQueue? _dispatcherQueue;
    private bool _disposed;

    public event EventHandler? OpenRequested;

    public event EventHandler? RefreshAllRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? ExitRequested;

    public void Start()
    {
        if (_trayIcon is not null || _disposed)
        {
            return;
        }

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The tray icon must be initialized on the UI thread.");

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ServerMonitorTray.svg");
        if (!File.Exists(iconPath))
        {
            throw new FileNotFoundException("The Server Monitor tray icon asset is missing.", iconPath);
        }

        TrayIcon? icon = null;
        try
        {
            icon = new TrayIcon(
                StableTrayIconId,
                iconPath,
                localizationService.GetString("TrayToolTip"));
            icon.Selected += OnSelected;
            icon.ContextMenu += OnContextMenu;
            icon.IsVisible = true;
            _trayIcon = icon;
        }
        catch
        {
            if (icon is not null)
            {
                icon.Selected -= OnSelected;
                icon.ContextMenu -= OnContextMenu;
                try
                {
                    icon.IsVisible = false;
                }
                catch (Exception cleanupException)
                {
                    logger.LogDebug(cleanupException, "Tray startup visibility cleanup failed.");
                }

                try
                {
                    icon.Dispose();
                }
                catch (Exception cleanupException)
                {
                    logger.LogDebug(cleanupException, "Tray startup disposal failed.");
                }
            }

            throw;
        }
        logger.LogInformation("System tray service started.");
    }

    public void StopSynchronously()
    {
        if (_trayIcon is null)
        {
            _disposed = true;
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("Synchronous tray cleanup must run on the UI thread.");
        }

        DisposeCore();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_trayIcon is null)
        {
            _disposed = true;
            return;
        }

        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            DisposeCore();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    DisposeCore();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            throw new InvalidOperationException("The UI dispatcher rejected tray cleanup.");
        }

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnSelected(object? sender, TrayIconEventArgs args) =>
        OpenRequested?.Invoke(this, EventArgs.Empty);

    private void OnContextMenu(object? sender, TrayIconEventArgs args)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(CreateMenuItem("TrayOpenMenuItem", OpenRequested));
        menu.Items.Add(CreateMenuItem("TrayRefreshAllMenuItem", RefreshAllRequested));
        menu.Items.Add(CreateMenuItem("TraySettingsMenuItem", SettingsRequested));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateMenuItem("TrayExitMenuItem", ExitRequested));
        args.Flyout = menu;
    }

    private MenuFlyoutItem CreateMenuItem(string resourceKey, EventHandler? requested)
    {
        var text = localizationService.GetString(resourceKey);
        var item = new MenuFlyoutItem { Text = text };
        AutomationProperties.SetName(item, text);
        item.Click += (_, _) => requested?.Invoke(this, EventArgs.Empty);
        return item;
    }

    private void DisposeCore()
    {
        var icon = Interlocked.Exchange(ref _trayIcon, null);
        if (icon is null)
        {
            _disposed = true;
            return;
        }

        icon.Selected -= OnSelected;
        icon.ContextMenu -= OnContextMenu;
        try
        {
            icon.IsVisible = false;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Tray visibility cleanup failed during shutdown.");
        }

        try
        {
            icon.Dispose();
        }
        catch (Exception exception)
        {
            // Tray cleanup must never prevent the authoritative host/window shutdown path.
            logger.LogWarning(exception, "Tray icon disposal failed during shutdown.");
        }
        finally
        {
            _disposed = true;
        }
        logger.LogInformation("System tray service stopped.");
    }
}
