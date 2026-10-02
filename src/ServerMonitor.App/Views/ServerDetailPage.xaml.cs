using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.4 D-UI4-DETAIL: the interim page that hosts the current <c>ServerFullCard</c> of one server (no redesign) with a
/// breadcrumb back to its origin. Replaced in UI.5. Fresh per navigation; the view model is disposed on Unloaded.
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
