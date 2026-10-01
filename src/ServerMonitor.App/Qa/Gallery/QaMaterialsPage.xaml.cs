using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ServerMonitor.App.Qa.Gallery;

/// <summary>
/// QA-ONLY (UI.2 S6). Surfaces on the Figma wallpaper. The toggle (or the "materials-fallback" page id) SIMULATES
/// transparency effects being off by swapping the glass sample to SaOpaqueFallbackBrush locally - it never touches the
/// system setting.
/// </summary>
public sealed partial class QaMaterialsPage : Page
{
    public QaMaterialsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        FallbackToggle.IsOn = e.Parameter is "fallback";
        Apply();
    }

    private void OnFallbackToggled(object sender, RoutedEventArgs e) => Apply();

    private void Apply()
    {
        GlassSample.Visibility = FallbackToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
        GlassFallbackSample.Visibility = FallbackToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }
}
