using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Infrastructure.Persistence;

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
                services.AddSingleton<IServerDialogService, ServerDialogService>();

                services.AddSingleton(ServerStorageOptions.ForCurrentUser());
                services.AddSingleton<IServerValidator, ServerValidator>();
                services.AddSingleton<IServerRepository, JsonServerRepository>();
                services.AddSingleton<IServerService, ServerService>();

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
