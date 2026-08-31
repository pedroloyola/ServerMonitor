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

        // A widget "open server" deep-link asks the (singleton) view model to focus a server; scroll its
        // card into view. The page and the view model are BOTH registered as singletons (App.xaml.cs), so
        // they share one app-length lifetime: the subscription (and the Loaded → LoadAsync refresh) is kept
        // for the app's lifetime and never removed on Unload. Unsubscribing on Unload would permanently stop
        // focus AND data refresh after the first navigation away from the reused page (H-1, Atlas review).
        ViewModel.ServerFocusRequested += OnServerFocusRequested;
        Loaded += OnLoaded;
    }

    public DashboardViewModel ViewModel { get; }

    /// <summary>Backs the discreet "compact mode" entry in the header; server VMs stay mode-agnostic.</summary>
    public WindowModeViewModel WindowMode { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private void OnServerFocusRequested(ServerCardViewModel card)
    {
        // Defer to after layout so the container exists, then bring the card into view (best-effort).
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ServersItemsControl.ContainerFromItem(card) is FrameworkElement container)
            {
                container.StartBringIntoView();
            }
        });
    }
}
