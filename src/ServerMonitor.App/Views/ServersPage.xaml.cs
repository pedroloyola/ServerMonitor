using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>UI.4 §3: the Servidores directory. Fresh per navigation; the view model is disposed on Unloaded.</summary>
public sealed partial class ServersPage : Page
{
    public ServersPage(ServersViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public ServersViewModel ViewModel { get; }

    private void OnBreadcrumbParentInvoked(object? sender, EventArgs e) => ViewModel.BackToOverviewCommand.Execute(null);

    // "Limpar pesquisa" disappears once it worked; focus returns to the search box (UI.3 pattern).
    private void OnClearSearchClick(object sender, RoutedEventArgs e) => FocusAfterAction.MoveTo((Control)sender, SearchBox);
}
