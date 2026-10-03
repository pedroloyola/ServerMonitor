using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.5: the Server Detail page of one server over its live card, with a breadcrumb back to its origin. Fresh per
/// navigation; the view model is disposed on Unloaded (and when navigation replaces the page). Code-behind is focus only:
/// back from Histórico / Serviços e containers the row that opened them takes focus again (taken once); otherwise the
/// first action (Atualizar) does, so keyboard and screen-reader users start on the page, not on the window chrome.
/// </summary>
public sealed partial class ServerDetailPage : Page, IServerDetailView, IDisposable
{
    public ServerDetailPage(ServerDetailViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        // Released on Unloaded AND when navigation replaces the page (a page replaced before Loaded never unloads).
        Unloaded += (_, _) => Dispose();
    }

    public ServerDetailViewModel ViewModel { get; }

    public void Load(Guid serverId, ServerDetailOrigin origin) => ViewModel.Load(serverId, origin);

    /// <summary>Idempotent: the view model unsubscribes once.</summary>
    public void Dispose()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }

    // Beacon C1 N2: "A atualizar..." is announced when a refresh starts (the text is the live region; it becomes visible
    // with the refresh, so the event is raised after that layout pass).
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ServerDetailViewModel.IsRefreshing) || !ViewModel.IsRefreshing)
        {
            return;
        }

        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            (FrameworkElementAutomationPeer.FromElement(RefreshingText) ?? FrameworkElementAutomationPeer.CreatePeerForElement(RefreshingText))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Control target = ViewModel.TakeReturnFocus() switch
        {
            ServerDetailReturnTarget.History => HistoryRow,
            ServerDetailReturnTarget.Workloads => WorkloadsRow,
            _ => RefreshButton
        };

        // Low priority: after the first layout pass, so the element is realized and can take focus.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!target.Focus(FocusState.Programmatic) && target != RefreshButton)
            {
                RefreshButton.Focus(FocusState.Programmatic);
            }

            target.StartBringIntoView();
        });
    }

    private void OnBreadcrumbParentInvoked(object? sender, EventArgs e) => ViewModel.GoBackCommand.Execute(null);
}
