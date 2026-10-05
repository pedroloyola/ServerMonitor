using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

public sealed partial class HistoryPage : Page, ServerMonitor.App.Services.IHistoryView, IDisposable
{
    public HistoryPage(HistoryViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ShellPageFocus.FocusHeading(this));
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public HistoryViewModel ViewModel { get; }

    /// <summary>Binds the page to a server and kicks off the initial history load.</summary>
    public void Load(Guid serverId, string serverName) => ViewModel.Load(serverId, serverName);

    public void Load(Guid? serverId, string serverName, bool fromDetail) => ViewModel.Load(serverId, serverName, fromDetail);

    public void LoadSidebar(Guid? lastDetailServer) => _ = ViewModel.LoadSidebarAsync(lastDetailServer);

    public void Dispose() => ViewModel.Dispose();

    // Beacon L1: the CTA disappears once it worked; focus goes to what it changed (the range, now "30 days"), not to the top.
    private void OnViewLast30DaysClick(object sender, RoutedEventArgs e) => FocusAfterAction.MoveTo((Control)sender, RangeLast30Days);
}
