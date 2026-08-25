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
                // The Debug-only visual health QA harness (--qa-health) replaces the data plane
                // with inert in-memory doubles instead, so no SSH/scheduling/persistence runs.
#if DEBUG
                var qaHealth = Qa.QaHealthComposition.IsRequested();
#else
                const bool qaHealth = false;
#endif
                if (!qaHealth)
                {
                    services.AddSingleton<IServerMonitoringStateStore, ServerMonitoringStateStore>();
                    services.AddSingleton(sp => new MonitoringEngine(
                        sp.GetRequiredService<IServerService>(),
                        sp.GetRequiredService<IServerMetricsStore>(),
                        sp.GetRequiredService<IServerMonitoringStateStore>(),
                        sp.GetRequiredService<ILogger<MonitoringEngine>>()));
                    services.AddSingleton<IMonitoringEngine>(sp => sp.GetRequiredService<MonitoringEngine>());
                    services.AddHostedService(sp => sp.GetRequiredService<MonitoringEngine>());
                }
#if DEBUG
                else
                {
                    Qa.QaHealthComposition.Apply(services);
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
            await ServicesHost.StartAsync();

            _mainWindow = ServicesHost.Services.GetRequiredService<MainWindow>();
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
            Exit();
        }
    }
}
