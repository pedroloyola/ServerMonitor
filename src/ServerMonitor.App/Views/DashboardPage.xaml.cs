using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

public sealed partial class DashboardPage : Page
{
    private readonly ServerEditorReturnFocus? _editorReturnFocus;

    public DashboardPage(
        DashboardViewModel viewModel,
        WindowModeViewModel windowMode,
        LanguageRecoveryHintViewModel languageHint,
        ServerEditorReturnFocus? editorReturnFocus = null)
    {
        InitializeComponent();
        _editorReturnFocus = editorReturnFocus;
        ViewModel = viewModel;
        WindowMode = windowMode;
        LanguageHint = languageHint;
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

    /// <summary>REL.0D: the one-time legacy language hint (closed for everyone else).</summary>
    public LanguageRecoveryHintViewModel LanguageHint { get; }

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
        // UI.7 B-5: back from the editor without saving, focus returns to the control that opened it.
        var editorOpener = _editorReturnFocus?.Take(NavigationDestination.Overview);
        if (ShellPageFocus.GetKeepSidebar(this)) return;
        // Cortex 7A m-3: "Adicionar" of a network suggestion has no page-level name; its row is found by the suggestion.
        if (EditorOpenerToken.TryGetSuggestion(editorOpener, out var suggestion) && FocusSuggestion(suggestion)) return;
        if (EditorReturnFocus.TryFocus(this, editorOpener)) return;
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

    // The suggestion's row in whichever list shows it (the section, or the first-server state), then its "Adicionar".
    // A suggestion that is gone (added, ignored, no longer seen) returns false: the page falls back to its own rule.
    private bool FocusSuggestion(string endpoint)
    {
        var suggestions = ViewModel.DiscoveredServers;
        var index = -1;
        for (var i = 0; i < suggestions.Count; i++)
        {
            if (string.Equals(suggestions[i].Endpoint, endpoint, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return false;
        }

        foreach (var repeater in new[] { DiscoveredRepeater, EmptyDiscoveredRepeater })
        {
            // UI.12 PEER-01: the row template is an SaGroupHost (the named group) around the row Grid.
            if (repeater.GetOrCreateElement(index) is SaGroupHost { Content: Panel row } && row.Children.OfType<Button>().FirstOrDefault() is { } add)
            {
                add.StartBringIntoView();
                if (add.Focus(FocusState.Programmatic))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void FocusContent() => ShellPageFocus.FocusHeading(this);

    // REL.0D: the language hint disappears once its decision is saved; focus returns to the page heading (top of the page,
    // where the restart notice now shows) instead of jumping down the page. A failed save keeps the hint and the focus.
    private void OnLanguageHintActionClick(object sender, RoutedEventArgs e)
    {
        var source = (Control)sender;
        // Low: after the layout pass that collapses the hint (and moves the stray focus), not before it.
        source.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!LanguageHint.IsOpen)
            {
                // The collapsing button already handed focus further down (and the viewport followed it): bring the top
                // back first - focusing the heading alone does not scroll it into view.
                ShellPageFocus.FindHeading(this)?.StartBringIntoView();
                ShellPageFocus.FocusHeading(this, force: true);
            }
        });
    }

    // "Limpar pesquisa" disappears once it worked; focus returns to the search box (UI.3 pattern).
    private void OnClearSearchClick(object sender, RoutedEventArgs e) => FocusAfterAction.MoveTo((Control)sender, OverviewSearchBox);
}
