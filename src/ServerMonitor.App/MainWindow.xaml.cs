using Microsoft.Extensions.Logging;
using System.ComponentModel;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Views;
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
    public ShellViewModel Shell { get; }
    public OnboardingViewModel Onboarding { get; }
    /// <summary>The compact chrome's view model ("Expandir", always-on-top, the Compact exits).</summary>
    public WindowModeViewModel ModeView { get; }
    private readonly WindowCloseCoordinator _closeCoordinator;
    private readonly IApplicationWindowController _windowController;
    private readonly AppWindowPlacementAdapter _placementAdapter;
    private readonly IWindowModeCoordinator _modeCoordinator;
    private readonly DashboardViewModel _dashboardViewModel;
    private readonly TrayService _trayService;
    private readonly BackupRestoreViewModel _backupRestore;
    private readonly ILogger<MainWindow> _logger;
    private readonly DispatcherQueueTimer _persistTimer;
    private bool _isEnforcingSize;
    private bool _usesOpaqueFallback;
    // Prism c1 P-2: how the user last drove the window (a keyboard entry shows the focus ring, anything else does not).
    private bool _lastInputWasKeyboard;
    private readonly Microsoft.UI.Xaml.Input.KeyEventHandler _keyInputObserver;
    private readonly Microsoft.UI.Xaml.Input.PointerEventHandler _pointerInputObserver;
    private FocusState _compactEntryFocusState;
    private int _compactEntryLayoutPasses;
    private bool _awaitingCompactEntryLayout;

    public MainWindow(
        INavigationService navigationService,
        ShellViewModel shell,
        OnboardingViewModel onboarding,
        IThemeService themeService,
        IWindowContext windowContext,
        ILocalizationService localizationService,
        IApplicationWindowController windowController,
        AppWindowPlacementAdapter placementAdapter,
        IWindowModeCoordinator modeCoordinator,
        WindowModeViewModel windowModeViewModel,
        DashboardViewModel dashboardViewModel,
        CompactPresentationViewModel compactPresentation,
        TrayService trayService,
        WindowCloseCoordinator closeCoordinator,
        BackupRestoreViewModel backupRestore,
        ILogger<MainWindow> logger)
    {
        ModeView = windowModeViewModel;
        InitializeComponent();
        _navigationService = navigationService;
        Shell = shell;
        Onboarding = onboarding;
        Sidebar.DataContext = shell;
        FirstRunView.Localization = localizationService;
        FirstRunView.DataContext = onboarding;
        Onboarding.PropertyChanged += OnOnboardingChanged;
        _navigationService.Navigated += OnShellNavigated;
        StandardRoot.SizeChanged += OnStandardSizeChanged;
        RootLayout.KeyDown += OnShellKeyDown;
        _keyInputObserver = (_, _) => _lastInputWasKeyboard = true;
        _pointerInputObserver = (_, _) => _lastInputWasKeyboard = false;
        RootLayout.AddHandler(UIElement.KeyDownEvent, _keyInputObserver, handledEventsToo: true);
        RootLayout.AddHandler(UIElement.PointerPressedEvent, _pointerInputObserver, handledEventsToo: true);
        _windowController = windowController;
        _placementAdapter = placementAdapter;
        _modeCoordinator = modeCoordinator;
        _dashboardViewModel = dashboardViewModel;
        _trayService = trayService;
        _closeCoordinator = closeCoordinator;
        _backupRestore = backupRestore;
        _logger = logger;
        Title = localizationService.GetString("AppWindowTitle");

        themeService.Attach(RootLayout);
        windowContext.Attach(this, RootLayout, ModalOverlayHost);
        windowController.Attach(this);
        _placementAdapter.Attach(this);
        navigationService.Initialize(ContentFrame);

        // UI.8: the Compact body is a view over the one dashboard's cards (CompactPresentationViewModel) and hands every
        // exit to the window-mode VM; both presentations show the same live state.
        CompactShellView.Initialize(compactPresentation, windowModeViewModel);
        CompactRoot.SizeChanged += OnCompactSizeChanged;

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
        SetTitleBar(ShellDragRegion);

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
            _usesOpaqueFallback = true;
            ApplyShellBackground();
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

    private async void OnRootLayoutLoaded(object sender, RoutedEventArgs e)
    {
        RootLayout.Loaded -= OnRootLayoutLoaded;
        // The XamlRoot (and its rasterization scale) is available now; recompute the compact caption
        // reserve on every DPI/scale change so the custom controls stay clear of the native buttons.
        if (RootLayout.XamlRoot is { } xamlRoot)
        {
            xamlRoot.Changed += OnXamlRootChanged;
        }

        UpdateCompactCaptionReserve();
        UpdateCompactTitleLayout();

        // Keep the standard dashboard navigated and its data loaded regardless of the starting mode,
        // so expanding from a cold compact start shows populated cards immediately.
#if DEBUG
        if (Qa.QaShellStartup.Present(Environment.GetCommandLineArgs(), Qa.QaShellStartup.StartFlag))
        {
            Onboarding.SuppressForActivation();
            await Qa.QaShellStartup.ApplyStartAsync(Environment.GetCommandLineArgs(), _navigationService, _dashboardViewModel);
        }
#endif
        var normalStart = OnboardingStartup.IsNormalStart(_navigationService.CurrentDestination, Program.LaunchMode);
        _startingOverview = _navigationService.CurrentDestination is null;
        _navigationService.EnsureInitialNavigation();
        _startingOverview = false;
        if (_modeCoordinator.CurrentMode == WindowMode.Compact)
        {
            _ = _dashboardViewModel.LoadAsync();
        }

        // M14.6: what startup recovery did with an interrupted restore, once per process. It waits for
        // the first window because the notice needs a XamlRoot; a headless start shows it here later.
        _ = _backupRestore.ShowStartupRecoveryOnceAsync();
        Onboarding.SetWindowMode(_modeCoordinator.CurrentMode);
        // The task observes and logs diagnosis failures; it cannot delay the earlier startup work.
        _ = Onboarding.OnMainWindowShownAsync(normalStart);
        // Prism c1 P-2: a launch straight into Compact puts focus where a mode change would - without a ring - instead of
        // leaving WinUI's first-tab-stop default ("Expandir", drawn with a keyboard ring).
        if (_modeCoordinator.CurrentMode == WindowMode.Compact)
        {
            BeginCompactEntryFocus(CompactEntryFocus.StateFor(enteredByKeyboard: false));
        }
    }

    private void OnWindowModeChanged(object? sender, WindowMode mode)
    {
        Onboarding.SetWindowMode(mode);
        ApplyShellBackground();
        if (mode == WindowMode.Compact)
        {
            StandardRoot.Visibility = Visibility.Collapsed;
            CompactRoot.Visibility = Visibility.Visible;
            SetTitleBar(CompactDragRegion);
            // The presenter's caption set is now the compact one (maximize disabled); size the
            // reserve to whatever the system actually reserves at the current DPI.
            UpdateCompactCaptionReserve();
            UpdateCompactTitleLayout();
        }
        else
        {
            CompactRoot.Visibility = Visibility.Collapsed;
            StandardRoot.Visibility = Visibility.Visible;
            SetTitleBar(ShellDragRegion);
        }

        UpdateCaptionButtonColors();
        FocusAfterModeChange(mode);
    }

    /// <summary>
    /// UI.8 RC-8 (deterministic, documented): entering Compact focuses the first server row, else the state block's real
    /// action, else "Expandir"; leaving it focuses the current page's heading (never the window root, UI.6 section 184).
    /// Keyboard focus only moves when the window already had it - a background mode change never steals focus.
    /// </summary>
    private void FocusAfterModeChange(WindowMode mode)
    {
        if (RootLayout.XamlRoot is not { } xamlRoot || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xamlRoot) is null)
        {
            return;
        }

        if (mode == WindowMode.Compact)
        {
            BeginCompactEntryFocus(CompactEntryFocus.StateFor(_lastInputWasKeyboard));
            return;
        }

        EndCompactEntryFocus();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ContentFrame.Content is Microsoft.UI.Xaml.Controls.Page page && !Onboarding.IsVisible)
            {
                ShellPageFocus.FocusHeading(page, force: true);
            }
        });
    }

    /// <summary>
    /// Prism c1 P-2: focuses the Compact entry target (CompactEntryFocus) once it is laid out. If the first row or the
    /// state action exists but is not realized yet, waits for the next layout pass of the compact body (one-shot
    /// LayoutUpdated, a bounded pass count - never a timer) instead of falling back to "Expandir" too early.
    /// </summary>
    private void BeginCompactEntryFocus(FocusState state)
    {
        EndCompactEntryFocus();
        _compactEntryFocusState = state;
        _compactEntryLayoutPasses = 0;
        if (!TryCompactEntryFocus())
        {
            _awaitingCompactEntryLayout = true;
            CompactShellView.LayoutUpdated += OnCompactEntryLayoutUpdated;
        }
    }

    private void EndCompactEntryFocus()
    {
        if (_awaitingCompactEntryLayout)
        {
            _awaitingCompactEntryLayout = false;
            CompactShellView.LayoutUpdated -= OnCompactEntryLayoutUpdated;
        }
    }

    private void OnCompactEntryLayoutUpdated(object? sender, object e)
    {
        _compactEntryLayoutPasses++;
        if (TryCompactEntryFocus())
        {
            EndCompactEntryFocus();
        }
    }

    /// <summary>True when focus was placed (or there is nothing left to wait for).</summary>
    private bool TryCompactEntryFocus()
    {
        var presentation = CompactShellView.Presentation;
        var repeater = CompactShellView.Repeater;
        var hasRows = presentation?.ShowsList == true && repeater.ItemsSourceView is { Count: > 0 };
        var firstRow = hasRows ? repeater.TryGetElement(0) as Microsoft.UI.Xaml.Controls.Control : null;
        var action = presentation is null ? null : CompactShellView.StateActionFor(presentation);
        var target = CompactEntryFocus.Decide(
            hasRows,
            firstRow is { IsLoaded: true },
            action is not null,
            action is { IsLoaded: true, Visibility: Visibility.Visible } && action.ActualWidth > 0,
            _compactEntryLayoutPasses);
        switch (target)
        {
            case CompactEntryFocus.Target.Wait:
                return false;
            case CompactEntryFocus.Target.FirstRow:
                firstRow!.Focus(_compactEntryFocusState);
                return true;
            case CompactEntryFocus.Target.StateAction:
                action!.Focus(_compactEntryFocusState);
                return true;
            default:
                CompactExpandButton.Focus(_compactEntryFocusState);
                return true;
        }
    }

    private void OnCompactSizeChanged(object sender, SizeChangedEventArgs args) => UpdateCompactTitleLayout();

    /// <summary>
    /// Prism R-3: decides, from MEASURED widths, whether "Expandir" shows its text, and whether the mark / wordmark fit
    /// (CompactTitleLayout). Runs on every size change of the compact root and after the caption reserve is recomputed.
    /// </summary>
    private void UpdateCompactTitleLayout()
    {
        if (CompactRoot.Visibility != Visibility.Visible || CompactRoot.ActualWidth <= 0)
        {
            return;
        }

        var infinite = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        CompactWordmark.Measure(infinite);
        CompactExpandText.Measure(infinite);
        var padding = CompactExpandButton.Padding;
        var buttonWithText = padding.Left + CompactExpandText.DesiredSize.Width + 6 + 16 + padding.Right;
        var available = CompactRoot.ActualWidth - CompactDragRegion.Padding.Left - CompactCaptionColumn.ActualWidth;
        var decision = CompactTitleLayout.Decide(available, CompactBrandMark.Size, CompactWordmark.DesiredSize.Width, buttonWithText);

        CompactExpandText.Visibility = decision.ShowExpandText ? Visibility.Visible : Visibility.Collapsed;
        CompactExpandButton.Padding = decision.ShowExpandText ? new Thickness(14, 0, 14, 0) : new Thickness(0);
        CompactExpandButton.Width = decision.ShowExpandText ? double.NaN : CompactTitleLayout.IconButtonWidth;
        CompactBrandMark.Visibility = decision.ShowMark ? Visibility.Visible : Visibility.Collapsed;
        CompactWordmark.Visibility = decision.ShowWordmark ? Visibility.Visible : Visibility.Collapsed;
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
            UpdateCompactTitleLayout();
        }
    }

    private void ApplyShellBackground()
    {
        // Styles keep ThemeResource live on this root. UI.8 D-UI8-12: Compact uses the SAME material as the shell (Figma
        // #0E0E0E@.62 / #FFF@.35 = SaWindowMaterialBrush), with the same opaque / High Contrast fallback.
        var key = _usesOpaqueFallback ? "SaOpaqueWindowBackgroundStyle" : "SaShellWindowBackgroundStyle";
        WindowBackground.Style = (Style)Application.Current.Resources[key];
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyShellBackground();
        UpdateCaptionButtonColors();
        // R-4: remount page CONTENT, never the Frame's Page (Unloaded would dispose per-visit VMs).
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ContentFrame.Content is Microsoft.UI.Xaml.Controls.Page page) SaThemeRefresh.Remount(page);
            SaThemeRefresh.Remount(Sidebar, Sidebar.Content, content => Sidebar.Content = content);
            SaThemeRefresh.Remount(FirstRunView, FirstRunView.Content, content => FirstRunView.Content = content);
            SaThemeRefresh.Remount(CompactShellView, CompactShellView.Content, content => CompactShellView.Content = content);
        });
    }

    private bool _startingOverview;
    private void OnStandardSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var width = ShellLayout.SidebarWidth(args.NewSize.Width);
        SidebarColumn.Width = new GridLength(width);
        Sidebar.SetRail(width == ShellLayout.RailWidth);
    }
    private void OnShellNavigated(object? sender, EventArgs args)
    {
        if (ContentFrame.Content is not Microsoft.UI.Xaml.Controls.Page page) return;
        var keepSidebar = _startingOverview || Shell.IsSidebarNavigation;
        ShellPageFocus.SetKeepSidebar(page, keepSidebar);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!ReferenceEquals(ContentFrame.Content, page) || Onboarding.IsVisible) return;
            if (keepSidebar) Sidebar.FocusSelected(); // Content pages own H1 / one-shot return focus after Loaded.
        });
    }

    private void OnOnboardingChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(OnboardingViewModel.IsVisible)) return;
        ShellSurface.Visibility = Onboarding.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        FirstRunView.Visibility = Onboarding.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        if (Onboarding.IsVisible) FirstRunView.FocusHeading();
        else if (ContentFrame.Content is Microsoft.UI.Xaml.Controls.Page page)
        {
            ShellPageFocus.SetKeepSidebar(page, false);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (ReferenceEquals(ContentFrame.Content, page)) ShellPageFocus.FocusHeading(page, force: true);
            });
        }
    }
    private void OnShellKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.F6 || Onboarding.IsVisible || _modeCoordinator.CurrentMode != WindowMode.Standard) return;
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootLayout.XamlRoot) as DependencyObject;
        var inSidebar = false;
        for (var node = focused; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, Sidebar)) { inSidebar = true; break; }
        if (inSidebar && ContentFrame.Content is Microsoft.UI.Xaml.Controls.Page page) ShellPageFocus.FocusHeading(page, force: true);
        else Sidebar.FocusSelected();
        args.Handled = true;
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

        if (_isEnforcingSize || _modeCoordinator.IsApplyingBounds)
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

        // UI.8 RC-2: both modes are resizable, so every user resize is held inside the ACTIVE mode's envelope (Standard:
        // its 560×640 floor; Compact: its DIP envelope at the current DPI, frame included). The limits come from the
        // coordinator, never from constants here, so leaving Compact can never leave its maximum on Standard.
        if (args.DidSizeChange)
        {
            EnforceSizeLimits(sender);
        }
    }

    private void EnforceSizeLimits(AppWindow sender)
    {
        // Cortex 8B gate N-4: a minimized window reports its iconized size (and no trustworthy DPI) - never clamp that.
        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            return;
        }

        var size = sender.Size;
        var (width, height) = _modeCoordinator.CurrentSizeLimits().Clamp(size.Width, size.Height);
        if (width == size.Width && height == size.Height)
        {
            return;
        }

        _isEnforcingSize = true;
        sender.Resize(new SizeInt32(width, height));
        _isEnforcingSize = false;
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
        Onboarding.PropertyChanged -= OnOnboardingChanged;
        _navigationService.Navigated -= OnShellNavigated;
        StandardRoot.SizeChanged -= OnStandardSizeChanged;
        CompactRoot.SizeChanged -= OnCompactSizeChanged;
        RootLayout.KeyDown -= OnShellKeyDown;
        RootLayout.RemoveHandler(UIElement.KeyDownEvent, _keyInputObserver);
        RootLayout.RemoveHandler(UIElement.PointerPressedEvent, _pointerInputObserver);
        EndCompactEntryFocus();
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
