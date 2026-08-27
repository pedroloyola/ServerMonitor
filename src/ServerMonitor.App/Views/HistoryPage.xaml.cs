using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

public sealed partial class HistoryPage : Page
{
    public HistoryPage(HistoryViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public HistoryViewModel ViewModel { get; }

    /// <summary>Binds the page to a server and kicks off the initial history load.</summary>
    public void Load(Guid serverId, string serverName) => ViewModel.Load(serverId, serverName);
}
