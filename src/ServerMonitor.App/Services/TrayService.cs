using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>
/// Coordinates tray commands with application-level services. It never performs SSH, creates a window,
/// or owns host shutdown: "Sair do ServerAlyzer" delegates to the one authoritative
/// <see cref="IAppLifecycleController.RequestExit"/>.
/// <para>
/// <b>The icon is the only way out of BACKGROUND</b> (M13 S2 §K; Vigil C2). In headless there is no
/// window either, so a process whose icon failed to appear would be monitoring with no user-reachable
/// stop — the A12 zombie by another route. Icon creation is therefore no longer fatal to startup, is
/// retried a bounded number of times on the injected clock, and, if it still cannot be created, the app
/// DEGRADES deterministically rather than continuing silently: it asks for a visible window and makes
/// closing it a true exit for this session, and if not even that is possible it exits. The user's
/// persisted preference is never rewritten by this — the degradation is per session.
/// </para>
/// </summary>
public sealed class TrayService(
    ITrayIconAdapter trayIcon,
    IApplicationWindowController windowController,
    IRefreshAllCoordinator refreshAllCoordinator,
    IServerAlertCoordinator alertCoordinator,
    IAppLifecycleController lifecycleController,
    ILogger<TrayService> logger,
    TimeProvider? timeProvider = null,
    int maxIconAttempts = TrayService.DefaultMaxIconAttempts,
    TimeSpan? iconRetryDelay = null) : IHostedService
{
    /// <summary>Shell_NotifyIcon most often fails transiently, while Explorer restarts.</summary>
    internal const int DefaultMaxIconAttempts = 3;

    internal static readonly TimeSpan DefaultIconRetryDelay = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _iconRetryDelay = iconRetryDelay ?? DefaultIconRetryDelay;
    private readonly object _sync = new();
    private bool _started;
    private bool _shutdownPrepared;
    private bool _exitRequested;
    private Task? _stopTask;

    /// <summary>
    /// True when the icon could not be created and the app fell back to a visible window. While set, the
    /// close button means a true exit, because the tray is not there to get back to.
    /// </summary>
    public bool ExitAffordanceDegraded { get; private set; }

    /// <summary>True while there is a usable way for the user to reach a true exit.</summary>
    public bool HasExitAffordance
    {
        get { lock (_sync) { return _started || ExitAffordanceDegraded; } }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_started || _shutdownPrepared)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            trayIcon.OpenRequested += OnOpenRequested;
            trayIcon.ToggleCompactRequested += OnToggleCompactRequested;
            trayIcon.RefreshAllRequested += OnRefreshAllRequested;
            trayIcon.SettingsRequested += OnSettingsRequested;
            trayIcon.ExitRequested += OnExitRequested;
        }

        for (var attempt = 1; attempt <= maxIconAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                trayIcon.Start();
                lock (_sync)
                {
                    _started = true;
                }

                return;
            }
            catch (Exception exception)
            {
                // NOT fatal (Vigil C2): monitoring is the product, and aborting startup would leave the
                // user with nothing at all. What must never happen is continuing with NO way out, which
                // the degradation below prevents.
                logger.LogWarning(
                    exception,
                    "The notification-area icon could not be created (attempt {Attempt} of {Attempts}).",
                    attempt,
                    maxIconAttempts);
            }

            if (attempt < maxIconAttempts)
            {
                await Task.Delay(_iconRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        DegradeWithoutTrayIcon();
    }

    /// <summary>
    /// No icon after every attempt. BACKGROUND is only a legitimate state while a true exit is reachable,
    /// so the app stops pretending it is: it asks for a visible window, whose close button now means a
    /// true exit, and if no window can be materialized at all it exits rather than monitoring
    /// unstoppably (M13 S2 §K).
    /// </summary>
    private void DegradeWithoutTrayIcon()
    {
        lock (_sync)
        {
            UnsubscribeLocked();
            _started = false;
            ExitAffordanceDegraded = true;
        }

        windowController.RestoreAndActivate();

        if (!windowController.IsMaterialized)
        {
            logger.LogError(
                "No notification-area icon and no window: exiting rather than monitoring with no way to stop.");
            lifecycleController.RequestExit(ExitReason.NoExitAffordance);
            return;
        }

        logger.LogWarning(
            "Running without a notification-area icon; closing the window now exits for this session.");
    }

    /// <summary>
    /// Removes the icon during a committed true exit (Vigil C3). Called from the exit sequence AFTER the
    /// process has committed to exiting and BEFORE the host drains, so the icon never outlives its
    /// usefulness by a whole drain and is never taken away while the app is still running.
    /// </summary>
    public void RemoveIconForExit()
    {
        lock (_sync)
        {
            if (_shutdownPrepared)
            {
                return;
            }

            _shutdownPrepared = true;
            UnsubscribeLocked();
            if (_started)
            {
                trayIcon.StopSynchronously();
                _started = false;
            }
        }
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

    /// <summary>
    /// "Sair do ServerAlyzer". It no longer closes the window and rides Window.Closed: it calls the one
    /// authoritative exit directly, which is what makes the headless exit (A12) possible at all — there
    /// is no window to close there.
    /// </summary>
    private void OnExitRequested(object? sender, EventArgs args)
    {
        lock (_sync)
        {
            if (_exitRequested)
            {
                return;
            }

            _exitRequested = true;
        }

        lifecycleController.RequestExit(ExitReason.TrayExit);
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
