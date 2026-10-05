using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

public sealed partial class WorkloadsPage : Page
{
    public WorkloadsPage(WorkloadsViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ShellPageFocus.FocusHeading(this));
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public WorkloadsViewModel ViewModel { get; }

    /// <summary>Binds the page to a server and renders its current workload snapshot.</summary>
    public void Load(Guid serverId, string serverName) => ViewModel.Load(serverId, serverName);

    // The breadcrumb parent (server name) goes where Back goes: the Dashboard until the server Detail exists (D-UI3-5).
    private void OnBreadcrumbParentInvoked(object? sender, EventArgs e) => ViewModel.BackCommand.Execute(null);

    // Beacon L1: "Clear search" disappears once it worked; focus goes back to the search box, not to the top of the page.
    private void OnClearSearchClick(object sender, RoutedEventArgs e) => FocusAfterAction.MoveTo((Control)sender, SearchBox);
}
