using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage(DashboardViewModel viewModel, WindowModeViewModel windowMode)
    {
        InitializeComponent();
        ViewModel = viewModel;
        WindowMode = windowMode;
        DataContext = ViewModel;

        // The page and the view model are BOTH registered as singletons (App.xaml.cs), so they share one app-length
        // lifetime: the Loaded → LoadAsync refresh is kept for the app's lifetime and never removed on Unload (H-1,
        // Atlas review). A widget "open server" deep-link no longer scrolls a card here: UI.4 D-UI4-DETAIL opens the
        // interim server page through the view model.
        Loaded += OnLoaded;
        SizeChanged += (_, _) => UpdateEmptyStateHeight();
        HeaderGrid.SizeChanged += (_, _) => UpdateEmptyStateHeight();
    }

    public DashboardViewModel ViewModel { get; }

    /// <summary>Backs the discreet "compact mode" entry in the header (D-UI4-NAV, until UI.6).</summary>
    public WindowModeViewModel WindowMode { get; }

    internal static double EmptyStateMinimumHeight(double usableHeight) => usableHeight >= 700 ? 620 : 0;

    private void UpdateEmptyStateHeight()
    {
        var usable = ActualHeight - PageRoot.Padding.Top - PageRoot.Padding.Bottom - HeaderGrid.ActualHeight - PageRoot.Spacing;
        FirstServerStateBlock.MinHeight = HiddenStateBlock.MinHeight = EmptyStateMinimumHeight(usable);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateEmptyStateHeight();
        await ViewModel.LoadAsync();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RestoreFocus);
    }

    /// <summary>
    /// Beacon r1 SHOULD-1: focus lands on the content, never on the window-mode button the window would pick by default
    /// (an Enter there flips the app to the compact window). Coming back from the interim page or Servidores, it lands on
    /// the row, the priority card or "Ver todos" that was used to leave.
    /// </summary>
    private void RestoreFocus()
    {
        var (target, serverId) = ViewModel.TakeReturnFocus();
        if (ShellPageFocus.GetKeepSidebar(this)) return;
        var focused = target switch
        {
            OverviewReturnTarget.ServerRow => FocusOverviewRow(serverId),
            OverviewReturnTarget.Priority => PriorityButton.Focus(FocusState.Programmatic),
            OverviewReturnTarget.DirectoryLink => DirectoryLinkButton.Visibility == Visibility.Visible && DirectoryLinkButton.Focus(FocusState.Programmatic),
            _ => false,
        };

        if (!focused)
        {
            FocusContent();
        }
    }

    private bool FocusOverviewRow(Guid serverId)
    {
        var rows = ViewModel.OverviewServers;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].ServerId == serverId)
            {
                return RepeaterFocus.FocusIndex(OverviewRepeater, i);
            }
        }

        return false;
    }

    private void FocusContent() => ShellPageFocus.FocusHeading(this);

    // "Limpar pesquisa" disappears once it worked; focus returns to the search box (UI.3 pattern).
    private void OnClearSearchClick(object sender, RoutedEventArgs e) => FocusAfterAction.MoveTo((Control)sender, OverviewSearchBox);
}
