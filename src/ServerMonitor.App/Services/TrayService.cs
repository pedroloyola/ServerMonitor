using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>
/// Coordinates tray commands with application-level services. It never performs SSH,
/// creates a window, or owns host shutdown. Exit closes the authoritative window, whose
/// existing Closed pipeline remains responsible for stopping the host.
/// </summary>
public sealed class TrayService(
    ITrayIconAdapter trayIcon,
    IApplicationWindowController windowController,
    IRefreshAllCoordinator refreshAllCoordinator,
    IServerAlertCoordinator alertCoordinator,
    ILogger<TrayService> logger) : IHostedService
{
    private readonly object _sync = new();
    private bool _started;
    private bool _shutdownPrepared;
    private bool _exitRequested;
    private Task? _stopTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_started || _shutdownPrepared)
            {
                return Task.CompletedTask;
            }

            cancellationToken.ThrowIfCancellationRequested();
            trayIcon.OpenRequested += OnOpenRequested;
            trayIcon.ToggleCompactRequested += OnToggleCompactRequested;
            trayIcon.RefreshAllRequested += OnRefreshAllRequested;
            trayIcon.SettingsRequested += OnSettingsRequested;
            trayIcon.ExitRequested += OnExitRequested;
            try
            {
                trayIcon.Start();
                _started = true;
            }
            catch
            {
                UnsubscribeLocked();
                throw;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Called by MainWindow.Closed on the UI thread before host shutdown.</summary>
    public void PrepareForShutdown()
    {
        lock (_sync)
        {
            if (_shutdownPrepared)
            {
                return;
            }

            _shutdownPrepared = true;
            windowController.BeginShutdown();
            alertCoordinator.BeginShutdown();
            refreshAllCoordinator.BeginShutdown();
            UnsubscribeLocked();
            trayIcon.StopSynchronously();
            _started = false;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Task stopTask;
        lock (_sync)
        {
            if (_stopTask is null)
            {
                var needsAsyncTrayCleanup = false;
                if (!_shutdownPrepared)
                {
                    _shutdownPrepared = true;
                    windowController.BeginShutdown();
                    alertCoordinator.BeginShutdown();
                    refreshAllCoordinator.BeginShutdown();
                    UnsubscribeLocked();
                    needsAsyncTrayCleanup = _started;
                    _started = false;
                }

                _stopTask = StopCoreAsync(needsAsyncTrayCleanup, cancellationToken);
            }

            stopTask = _stopTask;
        }

        return stopTask;
    }

    private async Task StopCoreAsync(bool needsAsyncTrayCleanup, CancellationToken cancellationToken)
    {
        if (needsAsyncTrayCleanup)
        {
            await trayIcon.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        await refreshAllCoordinator.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public void HandleWindowMinimized()
    {
        lock (_sync)
        {
            if (!_started || _shutdownPrepared)
            {
                return;
            }
        }

        windowController.HideForMinimize();
    }

    private void OnOpenRequested(object? sender, EventArgs args) => windowController.RestoreAndActivate();

    private void OnToggleCompactRequested(object? sender, EventArgs args) => windowController.ToggleCompactMode();

    private async void OnRefreshAllRequested(object? sender, EventArgs args)
    {
        try
        {
            var result = await refreshAllCoordinator.RefreshAllAsync().ConfigureAwait(false);
            logger.LogDebug(
                "Refresh All completed: {Succeeded}/{Requested} succeeded.",
                result.Succeeded,
                result.Requested);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Refresh All was cancelled during application shutdown.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Refresh All failed unexpectedly.");
        }
    }

    private void OnSettingsRequested(object? sender, EventArgs args) => windowController.OpenSettings();

    private void OnExitRequested(object? sender, EventArgs args)
    {
        lock (_sync)
        {
            if (_shutdownPrepared || _exitRequested)
            {
                return;
            }

            _exitRequested = true;
        }

        windowController.RequestClose();
    }

    private void UnsubscribeLocked()
    {
        trayIcon.OpenRequested -= OnOpenRequested;
        trayIcon.ToggleCompactRequested -= OnToggleCompactRequested;
        trayIcon.RefreshAllRequested -= OnRefreshAllRequested;
        trayIcon.SettingsRequested -= OnSettingsRequested;
        trayIcon.ExitRequested -= OnExitRequested;
    }
}
