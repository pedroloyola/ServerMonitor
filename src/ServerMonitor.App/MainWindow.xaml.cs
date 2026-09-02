using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Windowing;
using Windows.Graphics;

namespace ServerMonitor.App;

public sealed partial class MainWindow : Window
{
    private readonly INavigationService _navigationService;
    private readonly WindowCloseCoordinator _closeCoordinator;
    private readonly IApplicationWindowController _windowController;
    private readonly AppWindowPlacementAdapter _placementAdapter;
    private readonly IWindowModeCoordinator _modeCoordinator;
    private readonly DashboardViewModel _dashboardViewModel;
    private readonly TrayService _trayService;
    private readonly ILogger<MainWindow> _logger;
    private readonly DispatcherQueueTimer _persistTimer;
    private bool _isEnforcingMinimumSize;

    private const int MinimumWindowWidth = 560;
    private const int MinimumWindowHeight = 640;

    public MainWindow(
        INavigationService navigationService,
        IThemeService themeService,
        IWindowContext windowContext,
        ILocalizationService localizationService,
        IApplicationWindowController windowController,
        AppWindowPlacementAdapter placementAdapter,
        IWindowModeCoordinator modeCoordinator,
        WindowModeViewModel windowModeViewModel,
        DashboardViewModel dashboardViewModel,
        TrayService trayService,
        WindowCloseCoordinator closeCoordinator,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();
        _navigationService = navigationService;
        _windowController = windowController;
        _placementAdapter = placementAdapter;
        _modeCoordinator = modeCoordinator;
        _dashboardViewModel = dashboardViewModel;
        _trayService = trayService;
        _closeCoordinator = closeCoordinator;
        _logger = logger;
        Title = localizationService.GetString("AppWindowTitle");

        themeService.Attach(RootLayout);
        windowContext.Attach(this, RootLayout, ModalOverlayHost);
        windowController.Attach(this);
        _placementAdapter.Attach(this);
        navigationService.Initialize(ContentFrame);

        // The compact chrome/empty-state bind to the window-mode VM; the compact server list reuses
        // the one shared DashboardViewModel, so both presentations show the same live state.
        CompactRoot.DataContext = windowModeViewModel;
        CompactBody.DataContext = dashboardViewModel;

        _persistTimer = DispatcherQueue.CreateTimer();
        _persistTimer.Interval = TimeSpan.FromMilliseconds(700);
        _persistTimer.IsRepeating = false;
        _persistTimer.Tick += OnPersistTimerTick;

        _modeCoordinator.ModeChanged += OnWindowModeChanged;

        ConfigureWindow();
        // Apply the persisted mode and geometry now that the window and its displays are available.
        _modeCoordinator.Initialize();
        RootLayout.Loaded += OnRootLayoutLoaded;
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyWindowIcon();

        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        RootLayout.ActualThemeChanged += OnActualThemeChanged;
        AppWindow.Closing += OnAppWindowClosing;
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

    /// <summary>
    /// Sets the official ServerAlyzer icon on the window's title bar and Alt-Tab entry via
    /// <see cref="AppWindow.SetIcon(string)"/> (no P/Invoke). This is the window-scoped counterpart
    /// to the manifest visual assets that drive the taskbar and Start on a packaged run; together
    /// they keep every Windows surface on the same brand identity (M12). A missing/locked icon file
    /// must never prevent the window from opening, so failures are logged and swallowed.
    /// </summary>
    private void ApplyWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Images", "ServerAlyzer.ico");
        try
        {
            AppWindow.SetIcon(iconPath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not set the window icon from {IconPath}.", iconPath);
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
        // The XamlRoot (and its rasterization scale) is available now; recompute the compact caption
        // reserve on every DPI/scale change so the custom controls stay clear of the native buttons.
        if (RootLayout.XamlRoot is { } xamlRoot)
        {
            xamlRoot.Changed += OnXamlRootChanged;
        }

        UpdateCompactCaptionReserve();

        // Keep the standard dashboard navigated and its data loaded regardless of the starting mode,
        // so expanding from a cold compact start shows populated cards immediately.
        _navigationService.GoToDashboard();
        if (_modeCoordinator.CurrentMode == WindowMode.Compact)
        {
            _ = _dashboardViewModel.LoadAsync();
        }
    }

    private void OnWindowModeChanged(object? sender, WindowMode mode)
    {
        if (mode == WindowMode.Compact)
        {
            StandardRoot.Visibility = Visibility.Collapsed;
            CompactRoot.Visibility = Visibility.Visible;
            SetTitleBar(CompactDragRegion);
            // The presenter's caption set is now the compact one (maximize disabled); size the
            // reserve to whatever the system actually reserves at the current DPI.
            UpdateCompactCaptionReserve();
        }
        else
        {
            CompactRoot.Visibility = Visibility.Collapsed;
            StandardRoot.Visibility = Visibility.Visible;
            SetTitleBar(AppTitleBar);
        }

        UpdateCaptionButtonColors();
    }

    private void OnXamlRootChanged(Microsoft.UI.Xaml.XamlRoot sender, Microsoft.UI.Xaml.XamlRootChangedEventArgs args) =>
        UpdateCompactCaptionReserve();

    /// <summary>
    /// Reserves exactly the native caption-button width in the compact title bar, derived from the
    /// runtime <c>AppWindow.TitleBar.RightInset</c> (physical px) converted to DIPs. Never a hardcoded
    /// constant, so it is correct across DPI, scaling and caption changes. When the inset is not yet
    /// reported (0), the provisional width is kept and a later event recomputes it.
    /// </summary>
    private void UpdateCompactCaptionReserve()
    {
        var inset = _placementAdapter.GetCaptionRightInset();
        if (inset <= 0 || RootLayout.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        var reserved = Windowing.TitleBarInsetCalculator.ToReservedDips(inset, xamlRoot.RasterizationScale);
        if (reserved > 0)
        {
            CompactCaptionColumn.Width = new GridLength(reserved);
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateCaptionButtonColors();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange &&
            sender.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            // Persist the last good bounds before the window leaves the screen for the tray.
            _modeCoordinator.PersistCurrentBounds();
            _trayService.HandleWindowMinimized();
            return;
        }

        if (_isEnforcingMinimumSize || _modeCoordinator.IsApplyingBounds)
        {
            return;
        }

        if (!args.DidSizeChange && !args.DidPositionChange)
        {
            return;
        }

        // Keep the in-memory placement current on every move/resize; the disk write is debounced.
        _modeCoordinator.CaptureCurrentBounds();
        SchedulePersist();

        // The manual minimum-size floor applies to the resizable Standard window only; Compact is
        // non-resizable and drives its own bounds, so enforcing 560×640 there would corrupt it.
        if (args.DidSizeChange && _modeCoordinator.CurrentMode == WindowMode.Standard)
        {
            EnforceMinimumSize(sender);
        }
    }

    private void EnforceMinimumSize(AppWindow sender)
    {
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

    private void SchedulePersist()
    {
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    private void OnPersistTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        _modeCoordinator.PersistCurrentBounds();
    }

    /// <summary>
    /// The close button and Alt-F4 (M13 S2 §D). The window is never destroyed by the platform's own
    /// decision: the coordinator either cancels the close and hides the Dashboard (background monitoring
    /// on), or cancels it and routes into the one authoritative exit. The only close allowed through is
    /// the one <c>Application.Exit()</c> performs itself while already exiting.
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        try
        {
            args.Cancel = _closeCoordinator.HandleCloseRequest();
        }
        catch (Exception exception)
        {
            // A failure here must not trap the user in a window that cannot be closed.
            _logger.LogError(exception, "The window close decision failed; allowing the close.");
            args.Cancel = false;
        }
    }

    /// <summary>
    /// Local window cleanup ONLY (M13 S2 §E). It used to stop the monitoring host from here, which made a
    /// window event define process shutdown semantics; that now belongs exclusively to
    /// <see cref="IAppLifecycleController.RequestExit"/>, and this handler only ever runs as a
    /// CONSEQUENCE of it.
    /// </summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _persistTimer.Stop();
        _persistTimer.Tick -= OnPersistTimerTick;
        _modeCoordinator.PersistCurrentBounds();
        _modeCoordinator.ModeChanged -= OnWindowModeChanged;
        if (RootLayout.XamlRoot is { } xamlRoot)
        {
            xamlRoot.Changed -= OnXamlRootChanged;
        }

        AppWindow.Changed -= OnAppWindowChanged;
        AppWindow.Closing -= OnAppWindowClosing;
        RootLayout.ActualThemeChanged -= OnActualThemeChanged;
        Closed -= OnWindowClosed;
    }
}
