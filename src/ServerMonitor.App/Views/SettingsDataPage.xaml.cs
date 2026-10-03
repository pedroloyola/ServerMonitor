using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.5 §4: "Dados e servidores", the second Settings sub-page. A singleton like <see cref="SettingsPage"/>, sharing the
/// same singleton view models (the backup status, including a restore in progress, lives there and survives moving
/// between the two pages). Code-behind is view logic only: bringing sections into view.
/// </summary>
public sealed partial class SettingsDataPage : Page, ISettingsNavigationTarget
{
    public SettingsDataPage(SettingsViewModel viewModel, BackupRestoreViewModel backup)
    {
        // Set before InitializeComponent: the Backup section binds with x:Bind.
        Backup = backup;
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>Backs the Backup and restore section (M14.6).</summary>
    public BackupRestoreViewModel Backup { get; }

    /// <summary>Cortex #6: navigated to while already shown (no Loaded) — honour a pending About request now.</summary>
    public void OnNavigatedToAgain() => BringRequestedSectionIntoView();

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        BringRequestedSectionIntoView();
        Backup.PropertyChanged -= OnBackupPropertyChanged;
        Backup.PropertyChanged += OnBackupPropertyChanged;
        await ViewModel.LoadAsync();
    }

    private void OnUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        Backup.PropertyChanged -= OnBackupPropertyChanged;

    private void BringRequestedSectionIntoView()
    {
        ViewModel.NotifyDataNavigatedTo();
        if (ViewModel.IsAboutSectionRequested)
        {
            AboutSection.StartBringIntoView();
        }
    }

    /// <summary>The project page (the same URI the About section always opened).</summary>
    internal static readonly Uri GitHubUri = new("https://github.com/pedroloyola/ServerMonitor");

    private async void OnGitHubClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(GitHubUri);

    private void OnToastCloseRequested(object? sender, EventArgs e) => ViewModel.DismissToastCommand.Execute(null);

    // The outcome of a backup or restore appears below the buttons: bring it into view so it is never reported
    // off-screen (unchanged from the single Settings page, M14.6).
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
