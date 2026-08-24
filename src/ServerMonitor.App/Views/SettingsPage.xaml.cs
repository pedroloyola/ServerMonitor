using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        Loaded += OnLoaded;
    }

    public SettingsViewModel ViewModel { get; }

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await ViewModel.LoadAsync();
}
