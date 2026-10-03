using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>
/// "Definições", the General Settings sub-page (UI.5 §4; "Dados e servidores" is <see cref="SettingsDataPage"/>).
/// A singleton: code-behind is view logic only (bringing the Background section into view).
/// </summary>
public sealed partial class SettingsPage : Page, ISettingsNavigationTarget
{
    public SettingsPage(
        SettingsViewModel viewModel,
        WindowModeViewModel windowMode)
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

    /// <summary>
    /// Cortex #6: a Background request made while this page is ALREADY shown (tray loss / degradation toast with
    /// Settings open) gets no Loaded — the navigation calls this instead, so it is honoured now, not on the next visit.
    /// </summary>
    public void OnNavigatedToAgain() => BringRequestedSectionIntoView();

    /// <summary>Cortex B1 M-1: Loaded has run (the request can be honoured now); before that, Loaded consumes it.</summary>
    public bool IsReadyForSectionRequest => IsLoaded;

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Consume any pending "land on the Background section" request FIRST (M13 S2 §11): it is set by
        // the background notice's activation around the navigation, and it is what makes that activation
        // open on the right section instead of the top of Settings.
        BringRequestedSectionIntoView();
        await ViewModel.LoadAsync();
    }

    private void BringRequestedSectionIntoView()
    {
        ViewModel.NotifyNavigatedTo();
        if (ViewModel.IsBackgroundSectionRequested)
        {
            // M13 S2 §11 / Beacon C1 M4 (same pattern as About): in view AND focused on its toggle, after the layout pass.
            BackgroundSection.StartBringIntoView();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                BackgroundControl.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                BackgroundSection.StartBringIntoView();
            });
        }
    }
}
