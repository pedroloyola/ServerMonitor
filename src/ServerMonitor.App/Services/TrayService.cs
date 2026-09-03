using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ServerMonitor.App.Services;

/// <summary>
/// Coordinates tray commands with application-level services. It never performs SSH, creates a window,
/// or owns host shutdown: "Sair do ServerAlyzer" delegates to the one authoritative
/// <see cref="IAppLifecycleController.RequestExit"/>.
/// <para>
/// <b>It no longer decides whether a tray affordance exists.</b> It used to answer that from its own
/// <c>_started</c> flag, set after the library call returned — and the library discards the BOOL from
/// <c>Shell_NotifyIcon(NIM_ADD)</c>, so the flag proved nothing and a silent registration failure left a
/// headless process monitoring with no way out. Physical tray reliability moved to S2-T; this class asks
/// <see cref="TrayAffordanceLifecycle"/>, which consumes the positively established state, and otherwise
/// limits itself to wiring menu commands to services.
/// </para>
/// </summary>
public sealed class TrayService(
    ITrayIconAdapter trayIcon,
    IApplicationWindowController windowController,
    Action<TrayGuardedOperation> perform,
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
    /// Whether the icon object was created. Diagnostic ONLY: it is deliberately not the affordance
    /// signal, because a created object does not prove the shell holds the icon (M13 S2-T contract).
    /// </summary>
    public bool IconObjectCreated
    {
        get { lock (_sync) { return _started; } }
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

        // Whether an affordance exists is not this class's answer to give: TrayAffordanceLifecycle
        // consumes the S2-T state and decides. Exhausting the attempts here only means the icon object
        // could not be constructed, which is reported, not interpreted.
        logger.LogWarning("The notification-area icon object could not be created.");
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

        // ASKS; it does not hide. This used to call the window controller directly, guarded only by
        // "the service is started" -- so minimizing after a failed registration hid the window with no
        // tray icon to bring it back.
        perform(TrayGuardedOperation.HideForMinimize);
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
