using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage(DashboardViewModel viewModel, WindowModeViewModel windowMode)
    {
        InitializeComponent();
        ViewModel = viewModel;
        WindowMode = windowMode;
        DataContext = ViewModel;

        // The page and the view model are BOTH registered as singletons (App.xaml.cs), so they share one app-length
        // lifetime: the Loaded → LoadAsync refresh is kept for the app's lifetime and never removed on Unload (H-1,
        // Atlas review). A widget "open server" deep-link no longer scrolls a card here: UI.4 D-UI4-DETAIL opens the
        // interim server page through the view model.
        Loaded += OnLoaded;
    }

    public DashboardViewModel ViewModel { get; }

    /// <summary>Backs the discreet "compact mode" entry in the header (D-UI4-NAV, until UI.6).</summary>
    public WindowModeViewModel WindowMode { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    // "Limpar pesquisa" disappears once it worked; focus returns to the search box (UI.3 pattern).
    private void OnClearSearchClick(object sender, RoutedEventArgs e) => FocusAfterAction.MoveTo((Control)sender, OverviewSearchBox);
}
