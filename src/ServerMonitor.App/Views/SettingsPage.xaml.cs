using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage(
        SettingsViewModel viewModel,
        WindowModeViewModel windowMode,
        BackupRestoreViewModel backup)
    {
        // Set before InitializeComponent: the Backup section binds with x:Bind.
        Backup = backup;
        InitializeComponent();
        ViewModel = viewModel;
        WindowMode = windowMode;
        DataContext = ViewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>Backs the compact widget's always-on-top preference toggle.</summary>
    public WindowModeViewModel WindowMode { get; }

    /// <summary>Backs the Backup and restore section (M14.6).</summary>
    public BackupRestoreViewModel Backup { get; }

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

        Backup.PropertyChanged -= OnBackupPropertyChanged;
        Backup.PropertyChanged += OnBackupPropertyChanged;
        await ViewModel.LoadAsync();
    }

    private void OnUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        Backup.PropertyChanged -= OnBackupPropertyChanged;

    // The outcome of a backup or restore appears below the buttons, at the very end of the card: bring
    // it into view so it is never reported off-screen. Also when the flow ends (CanStart): the dialog
    // that just closed hands the focus back to its button, which scrolls the page to that button.
    private void OnBackupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BackupRestoreViewModel.IsStatusOpen) or nameof(BackupRestoreViewModel.CanStart)
            && Backup.IsStatusOpen)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                BackupStatusBar.UpdateLayout();
                BackupStatusBar.StartBringIntoView();
            });
        }
    }
}
