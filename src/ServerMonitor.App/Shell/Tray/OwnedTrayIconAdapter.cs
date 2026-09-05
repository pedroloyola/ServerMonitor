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
    private readonly TrayContextMenu _contextMenu;
    private readonly object _sync = new();

    private DispatcherQueue? _dispatcherQueue;
    private TrayHostWindow? _hostWindow;
    private NativeTrayRegistration? _registration;
    private TrayStateMachine? _machine;
    private ITrayLossConsumer? _lossConsumer;
    private ITrayGuardedOperations? _operations;
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
        _contextMenu = new TrayContextMenu(
            _localization, _themeService, _loggerFactory.CreateLogger<TrayContextMenu>());
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
    public void Perform(TrayGuardedOperation operation)
    {
        TrayStateMachine? machine;
        ITrayGuardedOperations? operations;
        lock (_sync)
        {
            machine = _machine;
            operations = _operations;
        }

        if (machine is null)
        {
            // Before initialization there is no machine and therefore no proof of anything, and a silent
            // no-op here would leave the window neither hidden nor closed. Fail to the same fallback the
            // machine uses, so the outcome is identical whichever side answers.
            operations?.Refuse(operation);
            return;
        }

        machine.Perform(operation);
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

            // The consumer is registered on the SOURCE at composition time and the machine is not built
            // until initialization, so it is held and handed over here. It is forwarded rather than
            // re-raised: re-raising it through the adapter would put it back in a multicast, which is the
            // whole defect.
            if (_lossConsumer is { } pendingConsumer)
            {
                machine.SetLossConsumer(pendingConsumer);
            }

            if (_operations is { } pendingOperations)
            {
                machine.SetGuardedOperations(pendingOperations);
            }

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
            _hostWindow = null;
            _registration = null;
            _machine = null;
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

    /// <summary>
    /// Hands the one authoritative loss consumer to the machine — directly, never through
    /// <see cref="StateChanged"/>. Single assignment on both sides.
    /// </summary>
    /// <summary>Hands the concrete guarded operations to the machine. Single assignment on both sides.</summary>
    public void SetGuardedOperations(ITrayGuardedOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        TrayStateMachine? machine;
        lock (_sync)
        {
            if (_operations is not null)
            {
                throw new InvalidOperationException(
                    "The guarded operations are already registered; there is exactly one set.");
            }

            _operations = operations;
            machine = _machine;
        }

        machine?.SetGuardedOperations(operations);
    }

    public void SetLossConsumer(ITrayLossConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        TrayStateMachine? machine;
        lock (_sync)
        {
            if (_lossConsumer is not null)
            {
                throw new InvalidOperationException(
                    "The authoritative loss consumer is already registered; there is exactly one.");
            }

            _lossConsumer = consumer;
            machine = _machine;
        }

        // Registered after initialization: hand it over now rather than waiting for a restart.
        machine?.SetLossConsumer(consumer);
    }

    // ------------------------------------------------------------------ flyout

    /// <summary>
    /// CV-9. A second context-menu request while a flyout is open produces nothing: no second flyout, no
    /// reposition of the open one, no episode touched, and no auxiliary window made visible.
    /// </summary>
    /// <summary>
    /// Shows the tray menu — a NATIVE shell menu, owned by the tray host window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The call is MODAL and returns the chosen command, which is why this method both opens the menu and
    /// dispatches the result. There is no close event to wait for and nothing can be left open: M13-QA-11
    /// was two liveness defects that existed only because the previous XAML flyout had to be told it had
    /// closed, and in three of four measured states nothing ever told it.
    /// </para>
    /// <para>
    /// The gate is still taken. With a modal menu a second request cannot arrive on this thread while one
    /// is up, so it is now belt as well as braces — kept because CV-9 is stated in terms of it, and its
    /// removal is a decision for the reviewers rather than a side effect of this fix.
    /// </para>
    /// </remarks>
    private void ShowFlyout(System.Drawing.Point anchor)
    {
        if (!_flyoutGate.TryOpen())
        {
            _logger.LogDebug("A tray menu is already open; the additional request is discarded.");
            return;
        }

        try
        {
            nint owner;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                owner = _hostWindow?.Handle ?? nint.Zero;
            }

            if (owner == nint.Zero)
            {
                _logger.LogError("The tray menu has no host window; the request is dropped.");
                return;
            }

            var chosen = _contextMenu.Show(owner, anchor);

            if (chosen is { } command)
            {
                OnFlyoutCommandInvoked(this, command);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The tray menu could not be shown.");
        }
        finally
        {
            // ONE release, on every path, because the menu is already closed by the time we get here.
            _flyoutGate.Close();
        }
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
        var result = _processTerminator.Terminate(1);
        if (!result.Requested)
        {
            _logger.LogError(
                "TerminateProcess refused the fail-safe escalation (Win32 error {Win32Error}); the process may survive.",
                result.Win32Error);
        }
    }
}
