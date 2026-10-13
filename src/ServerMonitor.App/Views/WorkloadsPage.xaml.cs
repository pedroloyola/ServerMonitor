using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <remarks>
/// UI.12 C-L2 (Cortex UI.12A): IDisposable like the other per-visit pages, so a page REPLACED before it was ever loaded (two
/// navigations in one dispatcher turn - it then never raises Unloaded) is still released by NavigationService.Show and its
/// view model leaves the singleton workload store. The view model's Dispose is idempotent (Unloaded may follow).
/// </remarks>
public sealed partial class WorkloadsPage : Page, IDisposable
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

    public void Dispose() => ViewModel.Dispose();

    /// <summary>Binds the page to a server and renders its current workload snapshot.</summary>
    public void Load(Guid serverId, string serverName) => ViewModel.Load(serverId, serverName);

    // The breadcrumb parent (server name) goes where Back goes: the Dashboard until the server Detail exists (D-UI3-5).
    private void OnBreadcrumbParentInvoked(object? sender, EventArgs e) => ViewModel.BackCommand.Execute(null);

    // Beacon L1: "Clear search" disappears once it worked; focus goes back to the search box, not to the top of the page.
    private void OnClearSearchClick(object sender, RoutedEventArgs e) => FocusAfterAction.MoveTo((Control)sender, SearchBox);
}
