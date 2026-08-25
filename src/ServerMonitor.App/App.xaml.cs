using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Collectors;
using ServerMonitor.Collectors.Linux;
using ServerMonitor.Collectors.MacOS;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Infrastructure.Collectors.Linux;
using ServerMonitor.Infrastructure.Collectors.MacOS;
using ServerMonitor.Infrastructure.Discovery;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Security;
using ServerMonitor.Infrastructure.SSH;

namespace ServerMonitor.App;

public partial class App : Application
{
    private Window? _mainWindow;

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
            .ConfigureServices(services =>
            {
                services.AddSingleton<ILocalizationService, LocalizationService>();
                services.AddSingleton<IThemeService, ThemeService>();
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<IWindowContext, WindowContext>();
                services.AddSingleton<IPrivateKeyFilePicker, PrivateKeyFilePicker>();
                services.AddSingleton<IServerConnectionStateStore, ServerConnectionStateStore>();
                services.AddSingleton<IServerDialogService, ServerDialogService>();
                services.AddSingleton(sp => new AppShutdownCoordinator(
                    () => ServicesHost,
                    sp.GetRequiredService<ILogger<AppShutdownCoordinator>>()));

                // M8 application-shell services. All Windows-specific behavior stays behind
                // fakeable boundaries; alert policy observes M6 but never performs SSH itself.
                services.AddSingleton(NotificationSettingsStorageOptions.ForCurrentUser());
                services.AddSingleton<INotificationSettingsService, JsonNotificationSettingsService>();
                services.AddSingleton<ApplicationWindowController>();
                services.AddSingleton<IApplicationWindowController>(sp =>
                    sp.GetRequiredService<ApplicationWindowController>());
                services.AddSingleton<RefreshAllCoordinator>();
                services.AddSingleton<IRefreshAllCoordinator>(sp =>
                    sp.GetRequiredService<RefreshAllCoordinator>());
                services.AddSingleton<ITrayIconAdapter, WinUIExTrayIconAdapter>();
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
                services.AddSingleton<LinuxMetricsCollector>();
                services.AddSingleton<MacOsMetricsCollector>();
                services.AddSingleton<IServerMetricsCollector, MetricsCollectorRouter>();
                services.AddSingleton<IServerMetricsStore, ServerMetricsStore>();

                // Automatic monitoring. One instance backs the IMonitoringEngine facade and
                // the hosted-service lifecycle, so the app starts/stops a single engine.
                // The Debug-only QA harnesses (--qa-health, --qa-discovery) replace the data plane
                // with inert in-memory doubles instead, so no SSH/scheduling/persistence runs.
#if DEBUG
                var qaHealth = Qa.QaHealthComposition.IsRequested();
                var qaDiscovery = Qa.QaDiscoveryComposition.IsRequested();
                var qaNotifications = Qa.QaNotificationComposition.IsRequested();
                var qaMode = qaHealth || qaDiscovery || qaNotifications;
#else
                const bool qaMode = false;
#endif
                if (!qaMode)
                {
                    services.AddSingleton<IServerMonitoringStateStore, ServerMonitoringStateStore>();
                    services.AddSingleton(sp => new MonitoringEngine(
                        sp.GetRequiredService<IServerService>(),
                        sp.GetRequiredService<IServerMetricsStore>(),
                        sp.GetRequiredService<IServerMonitoringStateStore>(),
                        sp.GetRequiredService<ILogger<MonitoringEngine>>()));
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
                services.AddTransient<MainWindow>();
            })
            .Build();

        ServicesHost.Services
            .GetRequiredService<ILocalizationService>()
            .InitializeFromSystem();
        InitializeComponent();
    }

    public static IHost ServicesHost { get; private set; } = null!;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // The tray hosted service is UI-thread-bound and the alert activation path must
            // restore this exact window. Attach it before starting the host, but activate it
            // only after every lifecycle participant has started successfully.
            _mainWindow = ServicesHost.Services.GetRequiredService<MainWindow>();
            await ServicesHost.StartAsync();
            _mainWindow.Activate();

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
                ServicesHost.Services.GetRequiredService<TrayService>().PrepareForShutdown();
                ServicesHost.Services.GetRequiredService<AppShutdownCoordinator>().Shutdown();
            }
            catch (Exception shutdownException)
            {
                ServicesHost.Services
                    .GetRequiredService<ILogger<App>>()
                    .LogError(shutdownException, "Server Monitor startup cleanup failed.");
            }
            Exit();
        }
    }
}
