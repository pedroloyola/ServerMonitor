using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.5: the Server Detail page of one server over its live card, with a breadcrumb back to its origin. Fresh per
/// navigation; the view model is disposed on Unloaded (and when navigation replaces the page). B1: minimal hosting XAML;
/// initial focus and the History/Workloads return focus (<see cref="ServerDetailViewModel.TakeReturnFocus"/>) are wired
/// to the Figma controls in B2.
/// </summary>
public sealed partial class ServerDetailPage : Page, IServerDetailView, IDisposable
{
    public ServerDetailPage(ServerDetailViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        // Released on Unloaded AND when navigation replaces the page (a page replaced before Loaded never unloads).
        Unloaded += (_, _) => Dispose();
    }

    public ServerDetailViewModel ViewModel { get; }

    public void Load(Guid serverId, ServerDetailOrigin origin) => ViewModel.Load(serverId, origin);

    /// <summary>Idempotent: the view model unsubscribes once.</summary>
    public void Dispose() => ViewModel.Dispose();

    private void OnBreadcrumbParentInvoked(object? sender, EventArgs e) => ViewModel.GoBackCommand.Execute(null);
}
