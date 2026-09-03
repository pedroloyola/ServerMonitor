using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Collectors;
using ServerMonitor.Collectors.Linux;
using ServerMonitor.Collectors.MacOS;
using ServerMonitor.Collectors.Workloads;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Monitoring;
using ServerMonitor.Core.Workloads;
using ServerMonitor.Infrastructure.Collectors.Linux;
using ServerMonitor.Infrastructure.Collectors.MacOS;
using ServerMonitor.Infrastructure.Collectors.Workloads;
using ServerMonitor.Infrastructure.Discovery;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;
using ServerMonitor.Infrastructure.SSH;
using ServerMonitor.App.Windowing;
using ServerMonitor.WidgetContract;
using ServerMonitor.ActivationContract;

namespace ServerMonitor.App;

public partial class App : Application
{
    private Window? _mainWindow;

    // Converges widget/protocol activation onto the single UI instance (ADR-018 §4). The executor runs
    // the navigation on the UI thread; the router buffers an intent that arrives before the shell is ready.
    private readonly ActivationRouter _activationRouter;

    public App()
    {
        ServicesHost = Microsoft.Extensions.Hosting.Host
            .CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices(services => ConfigureApplicationServices(services))
            .Build();

        ServicesHost.Services
            .GetRequiredService<ILocalizationService>()
            .InitializeFromSystem();
        _activationRouter = new ActivationRouter(ExecuteActivationIntent);
        // Attach the router to the single activation hand-off now that it exists: this atomically flushes
        // the latest intent buffered before this App object was built (the cold launch, or a redirect that
        // raced construction). The router buffers it internally until the shell signals ready (§M-1).
        Program.AttachActivationConsumer(_activationRouter.Route);

        // M13 S2 requirement 1. The process no longer ends because the last window closed, which is what
        // lets the Dashboard be hidden while monitoring continues (QA-8). It lands ONLY together with the
        // full exit path: on its own it produces the measured zombie — a live process with a stopped
        // engine, no tray and a frozen snapshot. Every true exit now reaches Application.Exit() exactly
        // once, and a termination watchdog backs it up.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        InitializeComponent();
    }

    /// <summary>
    /// Runs one activation intent on the UI thread: navigate to the dashboard, and for an OpenServer
    /// deep-link ask the dashboard to focus that server (best-effort; a removed server just shows the
    /// dashboard, §11). All navigation converges here — the widget never opens a second UI (§6).
    /// </summary>
    private void ExecuteActivationIntent(ActivationIntent intent)
    {
        // A headless process has no window yet, so "no window" can no longer mean "drop the intent":
        // RestoreAndActivate materializes one below. What is still needed is a dispatcher to run on.
        var dispatcher = _mainWindow?.DispatcherQueue ?? _uiDispatcherQueue;
        if (dispatcher is null)
        {
            return; // shell not ready; the router only executes after MarkReady, so this is defensive
        }

        dispatcher.TryEnqueue(() =>
        {
            try
            {
                // QA-2: a widget activation must SURFACE the Dashboard even if the app is in Compact mode.
                // RestoreAndActivate preserves the current presentation, so a Compact window would stay
                // Compact and never show the Dashboard/server. Force Standard first (Compact → Standard →
                // Dashboard → focus). No-op when already Standard; single-instance invariants are unchanged.
                var windowMode = ServicesHost.Services.GetRequiredService<IWindowModeCoordinator>();
                if (windowMode.CurrentMode == WindowMode.Compact)
                {
                    windowMode.SwitchTo(WindowMode.Standard);
                }

                ServicesHost.Services.GetRequiredService<IApplicationWindowController>().RestoreAndActivate();
                ServicesHost.Services.GetRequiredService<INavigationService>().GoToDashboard();

                var dashboard = ServicesHost.Services.GetRequiredService<DashboardViewModel>();
                if (intent.Kind == ActivationIntentKind.OpenServer && intent.ServerId is { } serverId)
                {
                    dashboard.FocusServer(serverId);
                }
                else
                {
                    // A dashboard intent supersedes an older, still-pending server-focus request (§M-3).
                    dashboard.ClearServerFocus();
                }
            }
            catch (Exception exception)
            {
                ServicesHost.Services.GetRequiredService<ILogger<App>>()
                    .LogError(exception, "Server Monitor could not process a widget activation.");
            }
        });
    }

    public static IHost ServicesHost { get; private set; } = null!;

    /// <summary>
    /// True once the process has committed to a true exit. Read by the activation gate in
    /// <c>Program</c>: EXIT WINS, so an activation that arrives during the drain is discarded rather than
    /// materializing UI or cancelling the shutdown.
    /// </summary>
    public bool IsExiting =>
        ServicesHost is not null
        && ServicesHost.Services.GetService<IAppLifecycleController>() is { IsExiting: true };

    /// <summary>
    /// The one call that ends the dispatcher. With <c>DispatcherShutdownMode.OnExplicitShutdown</c>
    /// nothing else terminates the process, which is exactly why hiding the window is now safe — and why
    /// every true-exit path must reach here. Marshalled to the UI thread, because a tray or watchdog
    /// caller may not be on it.
    /// </summary>
    private static void ExitApplication()
    {
        if (Current is not App app)
        {
            return;
        }

        var dispatcher = app._mainWindow?.DispatcherQueue ?? _uiDispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            app.Exit();
            return;
        }

        if (!dispatcher.TryEnqueue(app.Exit))
        {
            app.Exit();
        }
    }

    private static DispatcherQueue? _uiDispatcherQueue;

    /// <summary>
    /// Invoked when a second launch (or a redirected notification activation) is forwarded to this, the
    /// single primary instance (M12/ADR-017 §6). Restores and foregrounds the one authoritative window in
    /// its current presentation (Standard / Compact / tray) — never creating another. Any deep-link intent
    /// is routed separately through the activation hand-off (see <c>Program.OnActivated</c>), so this only
    /// restores the window. Marshals to the UI thread because <c>AppInstance.Activated</c> fires off it.
    /// </summary>
    public void RestoreOnRedirect()
    {
        // Headless has no window; the controller materializes one. Only a missing dispatcher (the shell
        // has not started at all) means there is nothing to do yet.
        var dispatcher = _mainWindow?.DispatcherQueue ?? _uiDispatcherQueue;
        if (dispatcher is null)
        {
            return;
        }

        var enqueued = dispatcher.TryEnqueue(() =>
        {
            try
            {
                ServicesHost.Services
                    .GetRequiredService<IApplicationWindowController>()
                    .RestoreAndActivate();
            }
            catch (Exception exception)
            {
                ServicesHost.Services
                    .GetRequiredService<ILogger<App>>()
                    .LogError(exception, "Server Monitor could not restore the window on reactivation.");
            }
        });

        if (!enqueued)
        {
            // The UI dispatcher is shutting down (the window is closing as this activation arrived).
            // Nothing to restore; the reactivation is intentionally dropped.
            ServicesHost.Services
                .GetRequiredService<ILogger<App>>()
                .LogWarning("Reactivation ignored: the UI dispatcher queue is shutting down.");
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _uiDispatcherQueue = DispatcherQueue.GetForCurrentThread();

            // A watchdog termination can only ever orphan one known temporary; clean exactly that one
            // (Vigil C10) before anything reads or writes the trust store.
            ServicesHost.Services.GetRequiredService<OrphanTemporaryCleaner>().CleanKnownHostTemporary(
                ServicesHost.Services.GetRequiredService<HostKeyTrustStorageOptions>().FilePath);

            // Headless (--background) starts monitoring and the tray but creates NO window: a
            // never-activated background process IS the BACKGROUND state. A later legitimate activation
            // materializes the Dashboard through this factory (M13 S2 §B.2).
            var windowController = ServicesHost.Services.GetRequiredService<ApplicationWindowController>();
            windowController.AttachWindowFactory(() => ServicesHost.Services.GetRequiredService<MainWindow>());

            var startsHeadless = Program.LaunchMode == LaunchMode.Background;
            if (!startsHeadless)
            {
                // The tray hosted service is UI-thread-bound and the alert activation path must
                // restore this exact window. Attach it before starting the host, but activate it
                // only after every lifecycle participant has started successfully.
                _mainWindow = ServicesHost.Services.GetRequiredService<MainWindow>();
            }

            await ServicesHost.StartAsync();

            // BLOCKING FIX (Prism, M13 S2-T). Resolving this is what SUBSCRIBES it to the affordance
            // source, and Evaluate() is what makes the process act on the state it already has. Until
            // now it was constructed lazily by WindowCloseCoordinator's lambda, so nothing evaluated it
            // until the user's FIRST close — and the two situations that matter both happen before that:
            //
            //   * a --background launch whose registration failed published Unavailable to NOBODY, and
            //     the process went on monitoring, invisible, with no way out. That is A12, the exact
            //     thing this slice exists to remove.
            //   * in the foreground, a Lost before the first close degraded nothing: the icon vanished
            //     silently and the next close quit with no explanation.
            //
            // Placed AFTER StartAsync so the tray has already attempted its registration and the state is
            // real rather than the fail-closed initial value, and BEFORE the activation branch so a
            // degraded foreground launch navigates to Settings -> Background while the window is still
            // hidden, instead of showing the Dashboard for a frame first.
            EvaluateStartupAffordance(ServicesHost.Services);

            if (!startsHeadless)
            {
                _mainWindow!.Activate();
            }
            else
            {
                ServicesHost.Services
                    .GetRequiredService<ILogger<App>>()
                    .LogInformation("Server Monitor started in background mode; no window was created.");
            }

            // The shell is ready: drain the single latest activation intent. Everything — this cold launch's
            // own activation and every redirect that arrived during startup — has already been funneled
            // through the one hand-off into the router, so exactly the most recent intent runs, once, with a
            // single consistent ordering (§M-1). No cold re-read, no second claim.
            _activationRouter.MarkReady();

            ServicesHost.Services
                .GetRequiredService<ILogger<App>>()
                .LogInformation("Server Monitor shell started.");
        }
        catch (Exception exception)
        {
            ServicesHost.Services
                .GetRequiredService<ILogger<App>>()
                .LogCritical(exception, "Server Monitor could not start.");
            try
            {
                // The same authoritative exit as every other path: one drain, one Exit, one watchdog.
                ServicesHost.Services
                    .GetRequiredService<IAppLifecycleController>()
                    .RequestExit(ExitReason.StartupFailure);
            }
            catch (Exception shutdownException)
            {
                ServicesHost.Services
                    .GetRequiredService<ILogger<App>>()
                    .LogError(shutdownException, "Server Monitor startup cleanup failed.");
                Exit();
            }
        }
    }
    /// <summary>
    /// Subscribes the degraded-session policy and makes the process act on the affordance state it
    /// already has.
    /// <para>
    /// An invocable method rather than two statements inside <c>OnLaunched</c>, for the same reason
    /// <see cref="ConfigureApplicationServices"/> is one: <c>OnLaunched</c> needs a XAML runtime, so
    /// anything only reachable from there is only reachable by a human with a desktop. Here the whole
    /// behaviour — resolve, subscribe, evaluate, degrade — runs in a test against the real composition.
    /// </para>
    /// </summary>
    internal static void EvaluateStartupAffordance(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Resolving it is half the fix: the constructor is what subscribes to the affordance source, so
        // a lifecycle nobody resolves is a policy that never hears anything. Evaluate() is the other
        // half: it acts on the state that already exists rather than waiting for a change.
        services.GetRequiredService<TrayAffordanceLifecycle>().Evaluate();
    }

    /// <summary>
    /// The composition root, as an invocable method rather than a lambda.
    /// <para>
    /// It was a lambda, and that made one architectural rule unprovable: CV-20 requires that nothing
    /// registers <c>INativeTrayRegistration</c> in the container, and a test can only check that by
    /// looking at the <see cref="ServiceDescriptor"/>s the root REALLY produces. Reading the source text
    /// instead would pass over a registration written any other way. Extracting the seam is what turns
    /// that condition from a claim into a check.
    /// </para>
    /// <para>
    /// Everything it touches is static, so a test may call it with a bare
    /// <see cref="ServiceCollection"/> and inspect the result without building a host, showing a window
    /// or starting anything.
    /// </para>
    /// </summary>
    internal static void ConfigureApplicationServices(IServiceCollection services)
    {
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IAppVersionProvider, AppVersionProvider>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IWindowContext, WindowContext>();
        services.AddSingleton<IPrivateKeyFilePicker, PrivateKeyFilePicker>();
        services.AddSingleton<IServerConnectionStateStore, ServerConnectionStateStore>();
        services.AddSingleton<IServerDialogService, ServerDialogService>();
        services.AddSingleton(sp => new AppShutdownCoordinator(
            () => ServicesHost,
            sp.GetRequiredService<ILogger<AppShutdownCoordinator>>()));

        // M13 S2 lifecycle. ONE authoritative exit, one owner of FOREGROUND/BACKGROUND/EXITING,
        // and a watchdog that guarantees the process really ends. The exit sequence is resolved
        // lazily by the controller because the tray and the notification service depend on the
        // controller in turn.
        // Registered as EXISTING INSTANCES owned by the process, never as container-created
        // singletons: the container must not be able to own, dispose or otherwise end the
        // watchdog, because the host it lives in is the very thing the watchdog guards against
        // (M13 S2 §F.3). Neither type implements IDisposable, so the container cannot dispose
        // them even by mistake.
        services.AddSingleton(Program.TerminationWatchdog);
        services.AddSingleton(Program.ProcessTerminator);
        services.AddSingleton<IExitSequence, ExitSequence>();
        // CV-17. The notice hangs off the exit path's EXISTING CAS: the controller calls this only on the
        // branch that won the transition to Exiting, so an exit the user asked for never produces it.
        services.AddSingleton(sp => new FailSafeExitNotice(
            sp.GetRequiredService<IUserNotificationService>,
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<ILogger<FailSafeExitNotice>>()));
        services.AddSingleton<IAppLifecycleController>(sp => new AppLifecycleController(
            sp.GetRequiredService<IExitSequence>,
            ExitApplication,
            sp.GetRequiredService<ITerminationWatchdog>(),
            sp.GetRequiredService<IProcessTerminator>(),
            sp.GetRequiredService<ILogger<AppLifecycleController>>(),
            Program.LaunchMode,
            terminationDeadline: null,
            onExitCommitted: sp.GetRequiredService<FailSafeExitNotice>().OnExitCommitted));
        services.AddSingleton(BackgroundSettingsStorageOptions.ForCurrentUser());
        services.AddSingleton<IBackgroundMonitoringSettingsService, JsonBackgroundMonitoringSettingsService>();
        services.AddSingleton<IBackgroundNoticePresenter, BackgroundNoticePresenter>();
        services.AddSingleton<IBackgroundDegradationNotice, BackgroundDegradationNotice>();

        // M13 S2-T. ONE owner of the icon, and the affordance state comes from the BOOL the shell
        // actually returned. OwnedTrayIconAdapter is registered once and exposed under both roles:
        // registering it twice would create two owners of one Shell_NotifyIcon registration, which is
        // the ambiguity this slice exists to remove. The old WinUIEx adapter is gone — not kept as a
        // fallback, because a fallback is a second owner by another name.
        services.AddSingleton(sp => new Shell.Tray.OwnedTrayIconAdapter(
            sp.GetRequiredService<IThemeService>(),
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<IAppLifecycleController>,
            sp.GetRequiredService<IProcessTerminator>(),
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<ITrayAffordanceSource>(sp =>
            sp.GetRequiredService<Shell.Tray.OwnedTrayIconAdapter>());
        services.AddSingleton<ITrayIconAdapter>(sp =>
            sp.GetRequiredService<Shell.Tray.OwnedTrayIconAdapter>());
        services.AddSingleton<TrayAffordanceLifecycle>();
        services.AddSingleton<OrphanTemporaryCleaner>();
        services.AddSingleton(sp => new WindowCloseCoordinator(
            sp.GetRequiredService<IAppLifecycleController>(),
            sp.GetRequiredService<IBackgroundMonitoringSettingsService>(),
            sp.GetRequiredService<IApplicationWindowController>(),
            sp.GetRequiredService<IBackgroundNoticePresenter>(),
            () => sp.GetRequiredService<TrayAffordanceLifecycle>().CanEnterBackground,
            sp.GetRequiredService<ILogger<WindowCloseCoordinator>>()));

        // M8 application-shell services. All Windows-specific behavior stays behind
        // fakeable boundaries; alert policy observes M6 but never performs SSH itself.
        services.AddSingleton(NotificationSettingsStorageOptions.ForCurrentUser());
        services.AddSingleton<INotificationSettingsService, JsonNotificationSettingsService>();
        services.AddSingleton<ApplicationWindowController>();
        services.AddSingleton<IApplicationWindowController>(sp =>
            sp.GetRequiredService<ApplicationWindowController>());

        // M9 compact widget mode. One window, two presentations. The placement store is the
        // real JSON file by default; the --qa-compact harness overrides it further below to
        // force Compact mode without touching the file. The adapter is the single native
        // window boundary; the coordinator sequences every Standard ⇄ Compact transition.
        services.AddSingleton(WindowPlacementStorageOptions.ForCurrentUser());
        services.AddSingleton<JsonWindowPlacementStore>();
        services.AddSingleton<IWindowPlacementStore>(sp =>
            sp.GetRequiredService<JsonWindowPlacementStore>());
        services.AddSingleton<AppWindowPlacementAdapter>();
        services.AddSingleton<IWindowPlacementAdapter>(sp =>
            sp.GetRequiredService<AppWindowPlacementAdapter>());
        services.AddSingleton<WindowModeCoordinator>();
        services.AddSingleton<IWindowModeCoordinator>(sp =>
            sp.GetRequiredService<WindowModeCoordinator>());
        services.AddSingleton<WindowModeViewModel>();
        services.AddSingleton<RefreshAllCoordinator>();
        services.AddSingleton<IRefreshAllCoordinator>(sp =>
            sp.GetRequiredService<RefreshAllCoordinator>());
        services.AddSingleton<TrayService>();
        services.AddSingleton<WindowsAppNotificationService>();
        services.AddSingleton<IUserNotificationService>(sp =>
            sp.GetRequiredService<WindowsAppNotificationService>());
        services.AddSingleton<ServerAlertCoordinator>();
        services.AddSingleton<IServerAlertCoordinator>(sp =>
            sp.GetRequiredService<ServerAlertCoordinator>());

        services.AddSingleton(ServerStorageOptions.ForCurrentUser());
        services.AddSingleton(HostKeyTrustStorageOptions.ForCurrentUser());
        services.AddSingleton<IServerValidator, ServerValidator>();
        services.AddSingleton<IServerRepository, JsonServerRepository>();
        services.AddSingleton<IServerService, ServerService>();
        services.AddSingleton<IServerCredentialStore, WindowsCredentialStore>();
        services.AddSingleton<IServerProfileService, ServerProfileService>();
        services.AddSingleton<IHostKeyTrustStore, JsonHostKeyTrustStore>();
        services.AddSingleton<SshConnectionService>();
        services.AddSingleton<ISshConnectionService>(sp => sp.GetRequiredService<SshConnectionService>());
        services.AddSingleton<ILinuxMetricsRemoteSource>(sp => sp.GetRequiredService<SshConnectionService>());
        services.AddSingleton<IMacOsMetricsRemoteSource>(sp => sp.GetRequiredService<SshConnectionService>());
        services.AddSingleton<IWorkloadRemoteSource>(sp => sp.GetRequiredService<SshConnectionService>());
        services.AddSingleton<LinuxMetricsCollector>();
        services.AddSingleton<MacOsMetricsCollector>();
        services.AddSingleton<IServerMetricsCollector, MetricsCollectorRouter>();
        services.AddSingleton<IServerMetricsStore, ServerMetricsStore>();

        // M10 local history. Default: unavailable. The real stack (in the non-QA branch
        // below) or a QA harness overrides this registration; every composition can resolve
        // these so the History UI degrades to "unavailable" gracefully.
        services.AddSingleton<IServerHistoryQueryService, NullServerHistoryQueryService>();
        services.AddSingleton<IHistoryMaintenanceService, NullHistoryMaintenanceService>();

        // M11 workloads. Default: an empty store + inert refresh, so the Workloads UI resolves
        // in every composition. The real collector service (non-QA branch) or the QA harness
        // overrides the refresh coordinator (and pre-populates the store).
        services.AddSingleton<IServerWorkloadStore, InMemoryServerWorkloadStore>();
        services.AddSingleton<IWorkloadRefreshCoordinator, NullWorkloadRefreshCoordinator>();

        // Automatic monitoring. One instance backs the IMonitoringEngine facade and
        // the hosted-service lifecycle, so the app starts/stops a single engine.
        // The Debug-only QA harnesses (--qa-health, --qa-discovery) replace the data plane
        // with inert in-memory doubles instead, so no SSH/scheduling/persistence runs.
#if DEBUG
        var qaHealth = Qa.QaHealthComposition.IsRequested();
        var qaDiscovery = Qa.QaDiscoveryComposition.IsRequested();
        var qaNotifications = Qa.QaNotificationComposition.IsRequested();
        var qaCompact = Qa.QaCompactComposition.IsRequested();
        var qaHistory = Qa.QaHistoryComposition.IsRequested();
        var qaWorkloads = Qa.QaWorkloadsComposition.IsRequested();
        var qaScreenshot = Qa.QaStoreScreenshotComposition.IsRequested();
        var qaMode = qaHealth || qaDiscovery || qaNotifications || qaCompact || qaHistory || qaWorkloads || qaScreenshot;
#else
        const bool qaMode = false;
#endif
        if (!qaMode)
        {
            // M10 history stack: recorder (IMonitoringCycleObserver) → bounded channel →
            // single writer → SQLite. A database failure never blocks monitoring (ADR-015).
            services.AddSingleton(HistoryStorageOptions.ForCurrentUser());
            services.AddSingleton<SqliteServerHistoryStore>();
            services.AddSingleton<IServerHistoryStore>(sp => sp.GetRequiredService<SqliteServerHistoryStore>());
            services.AddSingleton<HistorySampleChannel>();
            services.AddSingleton<HistoryRecorder>();
            services.AddSingleton<HistoryWriterService>();
            services.AddSingleton<IServerHistoryQueryService>(sp => new ServerHistoryQueryService(
                sp.GetRequiredService<IServerHistoryStore>(),
                sp.GetRequiredService<ILogger<ServerHistoryQueryService>>()));
            services.AddSingleton<IHistoryMaintenanceService, HistoryMaintenanceService>();

            // M11 read-only workloads (Docker + services). The cadence observer rides the same
            // cycle signal as history; the collector service runs SSH off the engine thread with
            // its own single-flight and concurrency limit. The real WorkloadCollector maps the
            // fixed read-only catalog (platform-infra) over the shared SSH session; a Debug
            // --qa-workloads run replaces it with a deterministic fake (registered later, wins).
            services.AddSingleton(WorkloadOptions.Default);
            services.AddSingleton<IWorkloadCollector>(sp =>
                new WorkloadCollector(sp.GetRequiredService<IWorkloadRemoteSource>()));
            services.AddSingleton<WorkloadRequestQueue>();
            services.AddSingleton(sp => new WorkloadCadencePolicy(
                sp.GetRequiredService<WorkloadOptions>().MinCadence));
            services.AddSingleton<WorkloadCadenceObserver>();
            services.AddSingleton<WorkloadCollectorService>();
            services.AddSingleton<IWorkloadRefreshCoordinator>(sp =>
                sp.GetRequiredService<WorkloadCollectorService>());

            // M13 widget snapshot (ADR-018 Slice 1): the recorder rides the SAME cycle signal
            // (no new timer/worker, §14/§15), builds a sanitized fleet snapshot from the live
            // stores, and writes %LOCALAPPDATA%\ServerMonitor\widget-state.json atomically. It is
            // best-effort and failure-isolated: a write fault never touches monitoring (§16). The
            // out-of-process widget provider (later slices) reads this file; nothing here starts
            // COM/SSH/a second engine.
            services.AddSingleton(WidgetStateOptions.ForCurrentUser());
            services.AddSingleton<IWidgetStateWriter>(sp => new AtomicWidgetStateWriter(
                sp.GetRequiredService<WidgetStateOptions>(),
                sp.GetRequiredService<ILogger<AtomicWidgetStateWriter>>()));
            services.AddSingleton(sp => new WidgetSnapshotRecorder(
                sp.GetRequiredService<IServerService>(),
                sp.GetRequiredService<IServerMonitoringStateStore>(),
                sp.GetRequiredService<IServerMetricsStore>(),
                sp.GetRequiredService<IWidgetStateWriter>(),
                sp.GetRequiredService<ILogger<WidgetSnapshotRecorder>>()));

            // Fan-out: the engine sees a single observer; history (M10), workloads (M11), and the
            // M13 widget snapshot all ride the cycle, each isolated from the others (§38). History
            // is first so its behavior is unchanged; the widget recorder is last (pure consumer).
            services.AddSingleton<IMonitoringCycleObserver>(sp => new CompositeMonitoringCycleObserver(
                new IMonitoringCycleObserver[]
                {
                    sp.GetRequiredService<HistoryRecorder>(),
                    sp.GetRequiredService<WorkloadCadenceObserver>(),
                    sp.GetRequiredService<WidgetSnapshotRecorder>()
                },
                sp.GetRequiredService<ILogger<CompositeMonitoringCycleObserver>>()));

            services.AddSingleton<IServerMonitoringStateStore, ServerMonitoringStateStore>();
            services.AddSingleton(sp => new MonitoringEngine(
                sp.GetRequiredService<IServerService>(),
                sp.GetRequiredService<IServerMetricsStore>(),
                sp.GetRequiredService<IServerMonitoringStateStore>(),
                sp.GetRequiredService<ILogger<MonitoringEngine>>(),
                timeProvider: null,
                options: null,
                cycleObserver: sp.GetRequiredService<IMonitoringCycleObserver>()));
            services.AddSingleton<IMonitoringEngine>(sp => sp.GetRequiredService<MonitoringEngine>());

            // Passive local network discovery (mDNS/DNS-SD, _ssh._tcp only). One instance
            // backs the IServerDiscoveryService facade and the hosted-service lifecycle.
            // The Tmds.MDns adapter is the fakeable Found/Updated/Removed seam; the ignored
            // decisions live in their own non-sensitive file, separate from servers.json.
            services.AddSingleton(IgnoredDeviceStorageOptions.ForCurrentUser());
            services.AddSingleton(MdnsServiceBrowserOptions.Default);
            services.AddSingleton<IIgnoredDeviceStore, JsonIgnoredDeviceStore>();
            services.AddSingleton<IMdnsServiceBrowser>(sp => new TmdsMdnsServiceBrowser(
                sp.GetRequiredService<ILogger<TmdsMdnsServiceBrowser>>(),
                sp.GetRequiredService<MdnsServiceBrowserOptions>()));
            services.AddSingleton(sp => new ServerDiscoveryService(
                sp.GetRequiredService<IMdnsServiceBrowser>(),
                sp.GetRequiredService<IIgnoredDeviceStore>(),
                sp.GetRequiredService<ILogger<ServerDiscoveryService>>()));
            services.AddSingleton<IServerDiscoveryService>(sp => sp.GetRequiredService<ServerDiscoveryService>());
        }
#if DEBUG
        else if (qaHealth)
        {
            Qa.QaHealthComposition.Apply(services);
        }
        else if (qaNotifications)
        {
            Qa.QaNotificationComposition.Apply(services);
        }
        else if (qaCompact)
        {
            Qa.QaCompactComposition.Apply(services);
        }
        else if (qaHistory)
        {
            Qa.QaHistoryComposition.Apply(services);
        }
        else if (qaWorkloads)
        {
            Qa.QaWorkloadsComposition.Apply(services);
        }
        else if (qaScreenshot)
        {
            Qa.QaStoreScreenshotComposition.Apply(services);
        }
        else
        {
            Qa.QaDiscoveryComposition.Apply(services);
        }
#endif

        // Hosted-service order is deliberate. The alert observer is live before M6 can
        // publish its first state; reverse shutdown stops M7/M6 before alert delivery and
        // notification registration. Tray is stopped last, with an earlier UI-thread
        // cleanup from MainWindow.Closed for the normal Exit path.
        services.AddHostedService(sp => sp.GetRequiredService<TrayService>());
        services.AddHostedService(sp => sp.GetRequiredService<WindowsAppNotificationService>());
        services.AddHostedService(sp => sp.GetRequiredService<ServerAlertCoordinator>());
        if (!qaMode)
        {
            // Registered before the engine so, on reverse-order shutdown, the writer stops
            // AFTER the engine stops producing and can drain the last samples (ADR-015 §9).
            services.AddHostedService(sp => sp.GetRequiredService<HistoryWriterService>());
            // Before the engine so, on reverse-order shutdown, the workload collector stops
            // AFTER the engine stops producing cycle signals and can drain what remains (§38).
            services.AddHostedService(sp => sp.GetRequiredService<WorkloadCollectorService>());
            services.AddHostedService(sp => sp.GetRequiredService<MonitoringEngine>());
            services.AddHostedService(sp => sp.GetRequiredService<ServerDiscoveryService>());
        }
#if DEBUG
        else if (qaNotifications)
        {
            // Registered last so the real alert coordinator has already captured the
            // in-memory Healthy baseline before the deterministic sequence begins.
            services.AddHostedService(sp =>
                sp.GetRequiredService<Qa.QaNotificationSequenceService>());
        }
#endif

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DashboardPage>();
        services.AddSingleton<SettingsPage>();
        // History is opened per-server, so a fresh page/VM each navigation (disposed on Unloaded).
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<HistoryPage>();
        // Workloads (M11) mirror History: opened per-server, fresh page/VM each navigation.
        services.AddTransient<WorkloadsViewModel>();
        services.AddTransient<WorkloadsPage>();
        services.AddTransient<MainWindow>();
    }

}
