using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Shell.Tray;

/// <summary>
/// The single owner of the notification-area icon, and the app's only
/// <see cref="ITrayAffordanceSource"/>.
/// <para>
/// It replaces <c>WinUIExTrayIconAdapter</c> outright. There is deliberately no fallback to the old
/// path: two owners of one icon is two answers to "is the affordance established", and the whole point of
/// this slice is that there is one, backed by the <c>BOOL</c> the shell actually returned.
/// </para>
/// <para>
/// It is also the join between the two halves. <see cref="TrayHostWindow"/> receives messages,
/// <see cref="TrayCallbackContract"/> decides whether to believe them, <see cref="TrayStateMachine"/>
/// decides what the app's state is, and this class translates a believed callback into the menu commands
/// <see cref="TrayService"/> already knows how to run. It holds no policy of its own.
/// </para>
/// </summary>
internal sealed class OwnedTrayIconAdapter : ITrayIconAdapter, ITrayAffordanceSource, IDisposable
{
    private readonly IThemeService _themeService;
    private readonly ILocalizationService _localization;
    private readonly Func<IAppLifecycleController> _lifecycleController;
    private readonly IProcessTerminator _processTerminator;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OwnedTrayIconAdapter> _logger;

    private readonly FlyoutReentrancyGate _flyoutGate = new();
    private readonly object _sync = new();

    private DispatcherQueue? _dispatcherQueue;
    private TrayHostWindow? _hostWindow;
    private NativeTrayRegistration? _registration;
    private TrayStateMachine? _machine;
    private TrayFlyoutWindow? _flyout;
    private bool _disposed;

    public event EventHandler? OpenRequested;

    public event EventHandler? RefreshAllRequested;

    public event EventHandler? ToggleCompactRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? ExitRequested;

    /// <summary>Raised when the affordance state changes. Forwarded from the state machine verbatim.</summary>
    public event EventHandler? StateChanged;

    internal OwnedTrayIconAdapter(
        IThemeService themeService,
        ILocalizationService localization,
        Func<IAppLifecycleController> lifecycleController,
        IProcessTerminator processTerminator,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _lifecycleController = lifecycleController ?? throw new ArgumentNullException(nameof(lifecycleController));
        _processTerminator = processTerminator ?? throw new ArgumentNullException(nameof(processTerminator));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = _loggerFactory.CreateLogger<OwnedTrayIconAdapter>();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The positively established state.
    /// </summary>
    /// <remarks>
    /// Before <see cref="Start"/> there is no machine and therefore no proof of anything, so this reports
    /// <see cref="TrayAffordanceState.Unavailable"/>. That is the fail-closed answer: a process that has
    /// not yet registered an icon has no exit affordance, and saying otherwise is the inference this
    /// whole contract exists to remove.
    /// </remarks>
    public TrayAffordanceState State
    {
        get
        {
            TrayStateMachine? machine;
            lock (_sync)
            {
                machine = _machine;
            }

            return machine?.State ?? TrayAffordanceState.Unavailable;
        }
    }

    /// <summary>
    /// Forwards the commit to the machine. Before <see cref="Start"/> there is no machine and therefore no
    /// affordance, so it refuses — the same fail-closed answer <see cref="State"/> gives.
    /// </summary>
    public bool TryEnterBackground(Action enterBackground)
    {
        ArgumentNullException.ThrowIfNull(enterBackground);

        TrayStateMachine? machine;
        lock (_sync)
        {
            machine = _machine;
        }

        return machine is not null && machine.TryEnterBackground(enterBackground);
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_machine is not null || _disposed)
            {
                return;
            }
        }

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The tray icon must be established on the UI thread.");

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ServerAlyzerTray.ico");

        var hostWindow = new TrayHostWindow(_loggerFactory.CreateLogger<TrayHostWindow>());
        NativeTrayRegistration? registration = null;
        TrayStateMachine? machine = null;

        try
        {
            registration = new NativeTrayRegistration(
                hostWindow.Handle,
                iconPath,
                _localization.GetString("TrayToolTip"),
                _loggerFactory.CreateLogger<NativeTrayRegistration>());

            machine = new TrayStateMachine(
                registration,
                RequestAuthoritativeExit,
                EscalateTermination,
                _timeProvider,
                _loggerFactory.CreateLogger<TrayStateMachine>(),
                limiter: null,
                marshalToUi: RunOnUiThread);

            hostWindow.CallbackReceived += OnCallbackReceived;
            hostWindow.TaskbarCreated += OnTaskbarCreated;
            hostWindow.DpiChanged += OnDpiChanged;
            machine.StateChanged += OnMachineStateChanged;

            lock (_sync)
            {
                _hostWindow = hostWindow;
                _registration = registration;
                _machine = machine;
            }
        }
        catch
        {
            // Nothing half-built survives: a partially constructed owner is a second owner.
            machine?.Dispose();
            registration?.Dispose();
            hostWindow.Dispose();
            throw;
        }

        // Establish() is what actually calls NIM_ADD, and its result is what makes State meaningful.
        machine.Establish();
        _logger.LogInformation("The owned tray registration was established.");
    }

    /// <summary>
    /// Releases the icon on the UI thread during a committed exit.
    /// </summary>
    /// <remarks>
    /// Release is the state machine's single terminal path: it dominates every in-flight effect,
    /// compensates an <c>Add</c> that was already inside the native call, and — if the cleanup cannot be
    /// verified — escalates to the authoritative exit rather than living on unverified. This method does
    /// not delete the icon itself, because a second deletion authority is exactly what that design
    /// removed.
    /// </remarks>
    public void StopSynchronously()
    {
        TrayStateMachine? machine;
        lock (_sync)
        {
            machine = _machine;
        }

        if (machine is null)
        {
            Dispose();
            return;
        }

        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("Synchronous tray cleanup must run on the UI thread.");
        }

        machine.Release();
        Dispose();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_machine is null)
            {
                _disposed = true;
                return;
            }
        }

        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            StopSynchronously();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    StopSynchronously();
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

    public void Dispose()
    {
        TrayHostWindow? hostWindow;
        NativeTrayRegistration? registration;
        TrayStateMachine? machine;
        TrayFlyoutWindow? flyout;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            hostWindow = _hostWindow;
            registration = _registration;
            machine = _machine;
            flyout = _flyout;
            _hostWindow = null;
            _registration = null;
            _machine = null;
            _flyout = null;
        }

        if (hostWindow is not null)
        {
            hostWindow.CallbackReceived -= OnCallbackReceived;
            hostWindow.TaskbarCreated -= OnTaskbarCreated;
            hostWindow.DpiChanged -= OnDpiChanged;
        }

        if (machine is not null)
        {
            machine.StateChanged -= OnMachineStateChanged;
        }

        flyout?.Dispose();
        machine?.Dispose();
        registration?.Dispose();
        hostWindow?.Dispose();
    }

    // ------------------------------------------------------------------ shell events

    private void OnCallbackReceived(object? sender, TrayCallback callback)
    {
        switch (callback.Action)
        {
            case TrayCallbackAction.Open:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                break;

            case TrayCallbackAction.ContextMenu:
                ShowFlyout(callback.Anchor);
                break;

            default:
                // Unreachable while TrayCallbackAction stays a closed two-value list, and left explicit
                // so adding a third value is a decision rather than a silent fall-through.
                _logger.LogDebug("An unhandled tray callback action was discarded.");
                break;
        }
    }

    private void OnTaskbarCreated(object? sender, EventArgs args)
    {
        TrayStateMachine? machine;
        lock (_sync)
        {
            machine = _machine;
        }

        // No filtering here on purpose. Whether this broadcast starts an episode at all is the frequency
        // limiter's decision inside the machine, and a second opinion here would be a second budget.
        machine?.NotifyTaskbarCreated();
    }

    /// <summary>
    /// Re-renders the icon for a new DPI, SERIALIZED against the machine's own shell calls.
    /// </summary>
    /// <remarks>
    /// It replaces and destroys an <c>HICON</c> and issues <c>NIM_MODIFY</c>. Called directly, it could
    /// overlap a recovery <c>NIM_ADD</c> — two unsynchronized callers on one icon, one of them freeing a
    /// handle the other may still be using. Routing it through the machine's gate makes the DPI update
    /// and the recovery mutually exclusive without giving anyone a second way to reach the capability.
    /// </remarks>
    private void OnDpiChanged(object? sender, uint dpi)
    {
        NativeTrayRegistration? registration;
        TrayStateMachine? machine;
        lock (_sync)
        {
            registration = _registration;
            machine = _machine;
        }

        if (registration is null)
        {
            return;
        }

        RouteShellUpdate(machine, () => registration.UpdateForDpi(dpi));
    }

    /// <summary>
    /// Sends a shell update that this adapter owns through the machine's gate, so it is serialized
    /// against the machine's own <c>NIM_ADD</c> and <c>NIM_DELETE</c>.
    /// </summary>
    /// <remarks>
    /// Extracted so the ROUTING is testable and not just the gate. A test that only proved
    /// <c>InvokeUnderShellGate</c> serializes proved a property of the machine, not that this adapter
    /// uses it — and a mutation that sent the DPI update straight to the shell left that test green.
    /// </remarks>
    internal static void RouteShellUpdate(TrayStateMachine? machine, Action update)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (machine is null)
        {
            // No machine means nothing else is touching the icon: there is no one to serialize against.
            update();
            return;
        }

        machine.InvokeUnderShellGate(update);
    }

    /// <summary>
    /// Hands a scheduled continuation to the UI thread, and reports whether it will run there.
    /// </summary>
    /// <remarks>
    /// This is the whole of CV-7/CV-8's topology guarantee: every <c>Shell_NotifyIcon</c> call happens on
    /// the UI thread, including the ones a retry timer starts, which is the thread CV-8's cost figures
    /// were measured on.
    /// <para>
    /// It returns <c>false</c> instead of falling back to running inline. The inline fallback was the
    /// first version and it cancelled the guarantee the main path establishes: it executed on the timer's
    /// own thread, so the topology held only when nothing went wrong, and a continuation there is exactly
    /// the second drainer the ordering work exists to exclude. The caller drops and logs.
    /// </para>
    /// <para>
    /// Running inline when we ALREADY have thread access is not a fallback — it is the UI thread.
    /// </para>
    /// </remarks>
    private bool RunOnUiThread(Action continuation)
    {
        var dispatcher = _dispatcherQueue;

        if (dispatcher is null)
        {
            // Start() resolves it before anything can be scheduled, so this is unreachable in practice;
            // refusing is the fail-closed answer rather than guessing which thread we are on.
            return false;
        }

        if (dispatcher.HasThreadAccess)
        {
            continuation();
            return true;
        }

        return dispatcher.TryEnqueue(() => continuation());
    }

    private void OnMachineStateChanged(object? sender, EventArgs args) =>
        StateChanged?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ flyout

    /// <summary>
    /// CV-9. A second context-menu request while a flyout is open produces nothing: no second flyout, no
    /// reposition of the open one, no episode touched, and no auxiliary window made visible.
    /// </summary>
    private void ShowFlyout(System.Drawing.Point anchor)
    {
        if (!_flyoutGate.TryOpen())
        {
            _logger.LogDebug("A tray flyout is already open; the additional request is discarded.");
            return;
        }

        TrayFlyoutWindow flyout;

        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    _flyoutGate.Close();
                    return;
                }

                _flyout ??= CreateFlyout();
                flyout = _flyout;
            }
        }
        catch (Exception exception)
        {
            // The gate is released here and nowhere else on this path: leaving it held would make the
            // menu unopenable for the rest of the session, which is worse than the failure itself.
            _flyoutGate.Close();
            _logger.LogError(exception, "The tray flyout could not be created.");
            return;
        }

        flyout.Show(anchor);
    }

    private TrayFlyoutWindow CreateFlyout()
    {
        var flyout = new TrayFlyoutWindow(
            _themeService, _localization, _loggerFactory.CreateLogger<TrayFlyoutWindow>());

        flyout.CommandInvoked += OnFlyoutCommandInvoked;
        flyout.Closed += (_, _) => _flyoutGate.Close();
        return flyout;
    }

    private void OnFlyoutCommandInvoked(object? sender, TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Open:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.ToggleCompact:
                ToggleCompactRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.RefreshAll:
                RefreshAllRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.Settings:
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.Exit:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    // ------------------------------------------------------------------ fail-safe sinks

    /// <summary>
    /// The state machine's fail-safe sink: an unverifiable cleanup is never an acceptable steady state,
    /// so it asks for the ONE authoritative exit rather than inventing a tray-specific way out.
    /// </summary>
    private void RequestAuthoritativeExit() =>
        // TrayCleanupUnverified, not NoExitAffordance: the two are different situations that happen to
        // end the same way. NoExitAffordance is "there is no way out at all"; this is "the icon may
        // still be there and we cannot prove it is gone". Only this one raises the CV-17 notice, and
        // only when this call is the one that commits the exit.
        _lifecycleController().RequestExit(ExitReason.TrayCleanupUnverified);

    /// <summary>
    /// Last resort, after the authoritative exit has been asked for and has not ended the process. This
    /// is the terminator S2 already owns — not a second terminal mechanism.
    /// </summary>
    private void EscalateTermination()
    {
        _logger.LogError("The authoritative exit did not end the process after a tray cleanup failure; terminating.");
        _processTerminator.Terminate(1);
    }
}
