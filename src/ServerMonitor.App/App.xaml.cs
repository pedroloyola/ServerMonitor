using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;

namespace ServerMonitor.App;

public partial class App : Application
{
    private Window? _mainWindow;

    public App()
    {
        InitializeComponent();
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

                services.AddTransient<MainWindowViewModel>();
                services.AddTransient<DashboardViewModel>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<DashboardPage>();
                services.AddTransient<SettingsPage>();
                services.AddTransient<MainWindow>();
            })
            .Build();
    }

    public static IHost ServicesHost { get; private set; } = null!;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        await ServicesHost.StartAsync();

        var localization = ServicesHost.Services.GetRequiredService<ILocalizationService>();
        localization.InitializeFromSystem();

        _mainWindow = ServicesHost.Services.GetRequiredService<MainWindow>();
        _mainWindow.Activate();

        ServicesHost.Services
            .GetRequiredService<ILogger<App>>()
            .LogInformation("Server Monitor shell started.");
    }
}
