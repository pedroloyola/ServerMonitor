using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;
using Windows.Graphics;

namespace ServerMonitor.App;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _navigationService;
    private readonly ILogger<MainWindow> _logger;
    private bool _isEnforcingMinimumSize;

    private const int MinimumWindowWidth = 560;
    private const int MinimumWindowHeight = 640;

    public MainWindow(
        INavigationService navigationService,
        IThemeService themeService,
        IWindowContext windowContext,
        ILocalizationService localizationService,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();
        _navigationService = navigationService;
        _logger = logger;
        Title = localizationService.GetString("AppWindowTitle");

        themeService.Attach(RootLayout);
        windowContext.Attach(this, RootLayout, ModalOverlayHost);
        navigationService.Initialize(ContentFrame);
        ConfigureWindow();
        RootLayout.Loaded += OnRootLayoutLoaded;
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.Resize(new SizeInt32(780, 760));
        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        RootLayout.ActualThemeChanged += OnActualThemeChanged;
        Closed += OnWindowClosed;
        UpdateCaptionButtonColors();

        try
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Desktop Acrylic is unavailable; the opaque fallback will be used.");
            RootLayout.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBackgroundBrush"];
        }
    }

    private void UpdateCaptionButtonColors()
    {
        var isLight = RootLayout.ActualTheme == ElementTheme.Light;
        AppWindow.TitleBar.ButtonForegroundColor = isLight ? Colors.Black : Colors.White;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = isLight ? Colors.DimGray : Colors.LightGray;
    }

    private void OnRootLayoutLoaded(object sender, RoutedEventArgs e)
    {
        RootLayout.Loaded -= OnRootLayoutLoaded;
        _navigationService.GoToDashboard();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateCaptionButtonColors();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || _isEnforcingMinimumSize)
        {
            return;
        }

        var size = sender.Size;
        var width = Math.Max(size.Width, MinimumWindowWidth);
        var height = Math.Max(size.Height, MinimumWindowHeight);
        if (width == size.Width && height == size.Height)
        {
            return;
        }

        _isEnforcingMinimumSize = true;
        sender.Resize(new SizeInt32(width, height));
        _isEnforcingMinimumSize = false;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        AppWindow.Changed -= OnAppWindowChanged;
        RootLayout.ActualThemeChanged -= OnActualThemeChanged;
        Closed -= OnWindowClosed;
    }
}
