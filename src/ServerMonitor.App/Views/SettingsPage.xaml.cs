using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel, WindowModeViewModel windowMode)
    {
        InitializeComponent();
        ViewModel = viewModel;
        WindowMode = windowMode;
        DataContext = ViewModel;
        Loaded += OnLoaded;
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>Backs the compact widget's always-on-top preference toggle.</summary>
    public WindowModeViewModel WindowMode { get; }

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await ViewModel.LoadAsync();
}
