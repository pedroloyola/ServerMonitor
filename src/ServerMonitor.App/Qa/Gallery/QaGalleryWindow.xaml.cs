using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using Windows.Graphics;

namespace ServerMonitor.App.Qa.Gallery;

/// <summary>
/// QA-ONLY (UI.2 S2). The component gallery window: fixed size in memory (never a placement store, so nothing
/// is read from or written to the user's window-placement.json), a Dark / Light / HC-sim theme switch, a banner
/// with the REAL system high-contrast state (read-only) and one tab per gallery page.
/// </summary>
public sealed partial class QaGalleryWindow : Window
{
    private const int GalleryWidth = 1440;
    private const int GalleryHeight = 900;

    /// <summary>Gallery page id -> page; internal so a test proves it matches <see cref="QaGalleryPolicy.Pages"/> exactly.</summary>
    internal static readonly IReadOnlyDictionary<string, (string Title, Type PageType, string? Parameter)> PageTypes =
        new Dictionary<string, (string, Type, string?)>(StringComparer.Ordinal)
        {
            ["tokens"] = ("Tokens", typeof(QaTokenProbePage), null),
            ["typography"] = ("Typography", typeof(QaTypographyPage), null),
            ["colors"] = ("Colors", typeof(QaColorsPage), null),
            ["spacing"] = ("Spacing & radius", typeof(QaSpacingPage), null),
            ["materials"] = ("Materials", typeof(QaMaterialsPage), null),
            ["status"] = ("Status", typeof(QaStatusPage), null),
            ["motion"] = ("Motion", typeof(QaMotionPage), null),
            ["icons"] = ("Icons", typeof(QaIconsPage), null),
            ["buttons"] = ("Buttons", typeof(QaButtonsPage), null),
            ["forms"] = ("Forms", typeof(QaFormsPage), null),
            ["navigation"] = ("Navigation", typeof(QaNavigationPage), null),
            ["popup-combo"] = ("Popup · ComboBox", typeof(QaPopupComboPage), null),
            ["data"] = ("Data", typeof(QaDataPage), null),
            ["feedback"] = ("Feedback", typeof(QaFeedbackPage), null),
            ["materials-fallback"] = ("Materials · fallback (sim)", typeof(QaMaterialsPage), "fallback"),
            ["popup-flyout"] = ("Popup · Flyout", typeof(QaPopupFlyoutPage), null),
            ["popup-dialog"] = ("Popup · Dialog (destructive)", typeof(QaPopupDialogPage), "destructive"),
            ["popup-dialog-confirm"] = ("Popup · Dialog (confirm)", typeof(QaPopupDialogPage), "confirm")
        };

    private readonly QaGalleryRequest _request;
    private readonly Microsoft.UI.System.ThemeSettings _themeSettings;
    private string _theme;
    private string _page;
    private bool _simulating;

    public QaGalleryWindow(QaGalleryRequest request)
    {
        _request = request;
        _theme = request.Theme ?? "dark";
        _page = request.Page ?? QaGalleryPolicy.Pages[0];
        InitializeComponent();

        PlaceFixed();

        // Read-only: the REAL contrast state comes from the system; the gallery never changes it.
        _themeSettings = Microsoft.UI.System.ThemeSettings.CreateForWindowId(AppWindow.Id);
        _themeSettings.Changed += (_, _) => DispatcherQueue.TryEnqueue(UpdateBanner);

        foreach (var id in QaGalleryPolicy.Pages)
        {
            PageList.Items.Add(new ListViewItem { Content = PageTypes[id].Title, Tag = id });
        }

        (_theme switch { "light" => LightThemeOption, "hc-sim" => HcSimThemeOption, _ => DarkThemeOption }).IsChecked = true;
        ApplyTheme(_theme);
        SelectPage(_page);
    }

    /// <summary>The page currently shown (the probe page during the self-check).</summary>
    internal Page? CurrentPage => PageFrame.Content as Page;

    internal FrameworkElement ThemeRoot => GalleryRoot;

    /// <summary>
    /// Shows the page with this id. Navigating always creates a NEW page instance, which is what lets the
    /// HC resource simulation take effect: the page's ThemeResources resolve against ContentHost's dictionaries.
    /// </summary>
    internal void SelectPage(string id)
    {
        _page = id;
        var item = PageList.Items.OfType<ListViewItem>().First(i => (string)i.Tag == id);
        if (!ReferenceEquals(PageList.SelectedItem, item))
        {
            PageList.SelectedItem = item; // raises OnPageSelectionChanged, which navigates
            return;
        }

        PageFrame.Navigate(PageTypes[id].PageType, PageTypes[id].Parameter);
    }

    /// <summary>dark / light set GalleryRoot.RequestedTheme. hc-sim keeps the current Dark/Light theme and
    /// copies every HighContrast token entry into SimulationHost's Dark and Light theme dictionaries.</summary>
    internal void ApplyTheme(string theme)
    {
        _theme = theme;
        var simulate = theme == "hc-sim";
        if (!simulate)
        {
            GalleryRoot.RequestedTheme = theme == "light" ? ElementTheme.Light : ElementTheme.Dark;
        }
        else if (GalleryRoot.RequestedTheme == ElementTheme.Default)
        {
            GalleryRoot.RequestedTheme = ElementTheme.Dark;
        }

        if (simulate != _simulating)
        {
            // Only entering/leaving the simulation recreates the page. A Dark <-> Light switch keeps the SAME page
            // instance, which is what proves runtime ThemeResource re-resolution.
            _simulating = simulate;
            QaHighContrastSimulation.Apply(SimulationHost, simulate);
            // R1 (Prism MF-4): simulate the canvas too - the HC SaCanvasBrush (system Window colour) behind the page and
            // no Figma wallpaper - so WindowText is never judged on a dark backdrop.
            QaWallpaper.Suppressed = simulate;
            SimulationHost.Background = simulate
                && QaHighContrastSimulation.ThemeEntries("HighContrast").TryGetValue("SaCanvasBrush", out var canvas)
                ? canvas as Microsoft.UI.Xaml.Media.Brush
                : null;
            if (PageFrame.Content is not null)
            {
                PageFrame.Navigate(PageTypes[_page].PageType, PageTypes[_page].Parameter);
            }
        }

        UpdateBanner();
    }

    /// <summary>--qa-tokens: run the F-1 self-check on the probe page, write the report and end the process
    /// with 0 (PASS), 2 (FAIL) or 3 (harness error).</summary>
    internal void RunTokenSelfCheckAndExit()
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            var exitCode = QaGalleryComposition.HarnessErrorExitCode;
            var outputDirectory = _request.OutputDirectory ?? QaGalleryComposition.DefaultOutputDirectory;
            try
            {
                var report = await new QaTokenSelfCheck(this).RunAsync();
                exitCode = report.ExitCode;
                Write(outputDirectory, report);
            }
            catch (Exception exception)
            {
                try
                {
                    Write(outputDirectory, QaTokenSelfCheckReport.HarnessError(exception));
                }
                catch
                {
                    // The exit code alone still reports the harness error.
                }
            }

            Environment.Exit(exitCode);
        });
    }

    private static void Write(string outputDirectory, QaTokenSelfCheckReport report)
    {
        // Vigil low: never write the report through a junction/symlink (the policy only sees the string).
        if (Qa.QaPathSafety.CrossesReparsePoint(outputDirectory))
        {
            throw new InvalidOperationException($"Refusing to write the QA report through a reparse point: {outputDirectory}");
        }

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            Path.Combine(outputDirectory, "tokens-selfcheck.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void PlaceFixed()
    {
        var size = new SizeInt32(GalleryWidth, GalleryHeight);
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var x = workArea.X + Math.Max(0, (workArea.Width - size.Width) / 2);
        var y = workArea.Y + Math.Max(0, (workArea.Height - size.Height) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, size.Width, size.Height));
    }

    private void UpdateBanner()
    {
        var real = _themeSettings.HighContrast
            ? $"REAL system high contrast: ON ({_themeSettings.HighContrastScheme})"
            : "REAL system high contrast: off";
        var mode = _theme == "hc-sim"
            ? "Gallery theme: HC-sim = RESOURCE SIMULATION (HighContrast token entries with the current system palette; page canvas = HC Window colour, wallpaper off; the gallery chrome is not simulated; NOT a contrast theme - use Settings > Accessibility > Contrast themes for real HC)."
            : $"Gallery theme: {_theme}.";
        HcBannerText.Text = real + "  ·  " + mode;
    }

    private void OnThemeOptionChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string theme } && theme != _theme && PageFrame is not null)
        {
            ApplyTheme(theme);
        }
    }

    private void OnPageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageList.SelectedItem is ListViewItem { Tag: string id })
        {
            _page = id;
            PageFrame.Navigate(PageTypes[id].PageType, PageTypes[id].Parameter);
        }
    }
}
