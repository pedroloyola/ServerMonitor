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
        Loaded += OnLoaded;
        WidthStates.CurrentStateChanging += OnWidthStateChanging;
        WidthStates.CurrentStateChanged += OnWidthStateChanged;
    }

    private int _reflowFocusIndex = -1;

    // Beacon r1 SHOULD-1: back from the interim page, focus returns to the row that opened it; otherwise to the search.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var index = ViewModel.TakeReturnFocusIndex();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!RepeaterFocus.FocusIndex(ServersRepeater, index))
            {
                SearchBox.Focus(FocusState.Programmatic);
            }
        });
    }

    // Beacon r1 SHOULD-5: Wide/Mid/Stacked swap the row template, which recreates the rows and would drop focus to the
    // top of the window; the focused row keeps focus across the reflow.
    private void OnWidthStateChanging(object sender, VisualStateChangedEventArgs e) =>
        _reflowFocusIndex = RepeaterFocus.FocusedIndex(ServersRepeater);

    private void OnWidthStateChanged(object sender, VisualStateChangedEventArgs e)
    {
        var index = _reflowFocusIndex;
        _reflowFocusIndex = -1;
        RepeaterFocus.FocusIndexLater(ServersRepeater, index);
    }

    public ServersViewModel ViewModel { get; }

    /// <summary>Idempotent: the view model unsubscribes once.</summary>
    public void Dispose() => ViewModel.Dispose();

    private void OnBreadcrumbParentInvoked(object? sender, EventArgs e) => ViewModel.BackToOverviewCommand.Execute(null);

    // "Limpar pesquisa" disappears once it worked; focus returns to the search box (UI.3 pattern).
    private void OnClearSearchClick(object sender, RoutedEventArgs e) => FocusAfterAction.MoveTo((Control)sender, SearchBox);
}
