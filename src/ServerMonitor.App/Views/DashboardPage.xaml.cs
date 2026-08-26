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
        Loaded += OnLoaded;
    }

    public DashboardViewModel ViewModel { get; }

    /// <summary>Backs the discreet "compact mode" entry in the header; server VMs stay mode-agnostic.</summary>
    public WindowModeViewModel WindowMode { get; }

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }
}
