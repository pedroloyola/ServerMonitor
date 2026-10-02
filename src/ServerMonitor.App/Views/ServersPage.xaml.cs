using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>UI.4 §3: the Servidores directory. Fresh per navigation; the view model is disposed on Unloaded.</summary>
public sealed partial class ServersPage : Page, IDisposable
{
    public ServersPage(ServersViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        // Released on Unloaded AND when navigation replaces the page (a page replaced before Loaded never unloads).
        Unloaded += (_, _) => Dispose();
    }

    public ServersViewModel ViewModel { get; }

    /// <summary>Idempotent: the view model unsubscribes once.</summary>
    public void Dispose() => ViewModel.Dispose();

    private void OnBreadcrumbParentInvoked(object? sender, EventArgs e) => ViewModel.BackToOverviewCommand.Execute(null);

    // "Limpar pesquisa" disappears once it worked; focus returns to the search box (UI.3 pattern).
    private void OnClearSearchClick(object sender, RoutedEventArgs e) => FocusAfterAction.MoveTo((Control)sender, SearchBox);
}
