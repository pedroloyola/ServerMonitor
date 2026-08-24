using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using Windows.Graphics;

namespace ServerMonitor.App;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _navigationService;
    private readonly ILogger<MainWindow> _logger;

    public MainWindow(
        MainWindowViewModel viewModel,
        INavigationService navigationService,
        IThemeService themeService,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _navigationService = navigationService;
        _logger = logger;
        RootLayout.DataContext = ViewModel;

        themeService.Attach(RootLayout);
        navigationService.Initialize(ContentFrame);
        ConfigureWindow();
    }

    public MainWindowViewModel ViewModel { get; }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.Resize(new SizeInt32(720, 760));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Mica is unavailable; the theme fallback background will be used.");
        }
    }

    private void OnNavigationLoaded(object sender, RoutedEventArgs e)
    {
        if (ShellNavigation.MenuItems.FirstOrDefault() is NavigationViewItem dashboardItem)
        {
            ShellNavigation.SelectedItem = dashboardItem;
        }

        _navigationService.NavigateTo<DashboardPage>();
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string destination)
        {
            return;
        }

        if (destination == "settings")
        {
            _navigationService.NavigateTo<SettingsPage>();
            return;
        }

        _navigationService.NavigateTo<DashboardPage>();
    }
}
