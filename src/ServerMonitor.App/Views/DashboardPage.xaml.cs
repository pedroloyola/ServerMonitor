using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        Loaded += OnLoaded;
    }

    public DashboardViewModel ViewModel { get; }

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }
}
