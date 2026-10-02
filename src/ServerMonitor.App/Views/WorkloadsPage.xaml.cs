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
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public WorkloadsViewModel ViewModel { get; }

    /// <summary>Binds the page to a server and renders its current workload snapshot.</summary>
    public void Load(Guid serverId, string serverName) => ViewModel.Load(serverId, serverName);

    // The breadcrumb parent (server name) goes where Back goes: the Dashboard until the server Detail exists (D-UI3-5).
    private void OnBreadcrumbParentInvoked(object? sender, EventArgs e) => ViewModel.BackCommand.Execute(null);
}
