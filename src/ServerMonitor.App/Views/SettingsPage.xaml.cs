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

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Consume any pending "land on the Background section" request FIRST (M13 S2 §11): it is set by
        // the background notice's activation just before the window is shown, and it is what makes that
        // activation open on the right section instead of the top of Settings.
        ViewModel.NotifyNavigatedTo();
        if (ViewModel.IsBackgroundSectionRequested)
        {
            BackgroundSection.StartBringIntoView();
        }

        await ViewModel.LoadAsync();
    }
}
