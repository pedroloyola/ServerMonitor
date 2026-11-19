using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.5: the Server Detail page of one server over its live card, with a breadcrumb back to its origin. Fresh per
/// navigation; the view model is disposed on Unloaded (and when navigation replaces the page). Code-behind is focus only:
/// back from Histórico / Serviços e containers the row that opened them takes focus again (taken once); otherwise the
/// first action (Atualizar) does, so keyboard and screen-reader users start on the page, not on the window chrome.
/// </summary>
public sealed partial class ServerDetailPage : Page, IServerDetailView, IDisposable
{
    private readonly ServerEditorReturnFocus? _editorReturnFocus;
    private readonly ShellContributionHost? _contributions;
    private readonly List<SaListRow> _serverPageRows = [];

    public ServerDetailPage(
        ServerDetailViewModel viewModel,
        ServerEditorReturnFocus? editorReturnFocus = null,
        ShellContributionHost? contributions = null)
    {
        InitializeComponent();
        _editorReturnFocus = editorReturnFocus;
        _contributions = contributions;
        ViewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ActionsFocusRequested += OnActionsFocusRequested;
        // Released on Unloaded AND when navigation replaces the page (a page replaced before Loaded never unloads).
        Unloaded += (_, _) => Dispose();
    }

    public ServerDetailViewModel ViewModel { get; }

    public void Load(Guid serverId, ServerDetailOrigin origin)
    {
        ViewModel.Load(serverId, origin);
        AddContributedSections(serverId);
        AddServerPageRows();
    }

    /// <summary>
    /// M16: one rich row on the Explorar card per contributed server page, like Histórico and Serviços e containers (the
    /// same SaListRow, a SaIcon* glyph when the module gives none). The row only asks the view model to open the page; no
    /// module code runs here (the host already resolved titles). With none the card keeps its two rows.
    /// </summary>
    private void AddServerPageRows()
    {
        foreach (var row in _serverPageRows)
        {
            ExploreRows.Children.Remove(row);
        }

        _serverPageRows.Clear();
        if (_contributions is null)
        {
            return;
        }

        foreach (var page in _contributions.ServerPageEntries)
        {
            var row = new SaListRow
            {
                Variant = SaListRowVariant.Rich,
                Title = page.Title,
                Detail = page.Detail ?? string.Empty,
                IconData = page.IconPathData ?? (string)Application.Current.Resources["SaIconArrowRight01Data"],
                Tag = page.Id,
            };
            var id = page.Id;
            row.Click += (_, _) => ViewModel.OpenServerPage(id);
            _serverPageRows.Add(row);
            ExploreRows.Children.Add(row);
        }
    }

    /// <summary>
    /// M16.1: the sections feature modules contribute to this server's page, each a titled glass card like the built-in
    /// ones. The host creates their views for this server now (a factory that failed was already left out) and each is
    /// placed through <see cref="ShellContributionHost.TryAttach"/>, so a view that fails when it is added leaves out
    /// that section only. With none the panel stays collapsed.
    /// </summary>
    private void AddContributedSections(Guid serverId)
    {
        ContributedSections.Children.Clear();
        if (_contributions is not null)
        {
            foreach (var section in _contributions.CreateServerDetailSections(serverId))
            {
                _contributions.TryAttach(section, view =>
                {
                    var title = new TextBlock { Text = section.Title, Style = (Style)Application.Current.Resources["SaCardTitleTextStyle"] };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
                    var block = new StackPanel { Spacing = 16 };
                    block.Children.Add(title);
                    block.Children.Add((UIElement)view);
                    var card = new Border { Style = (Style)Application.Current.Resources["SaGlassSurfaceStyle"], Child = block };
                    ContributedSections.Children.Add(card); // last: a card that failed above never reaches the page
                });
            }
        }

        ContributedSections.Visibility = ContributedSections.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Idempotent: the view model unsubscribes once.</summary>
    public void Dispose()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.ActionsFocusRequested -= OnActionsFocusRequested;
        ViewModel.Dispose();
    }

    private void OnSavedToastCloseRequested(object? sender, EventArgs e) => ViewModel.DismissSavedToast();

    // An Ocultar / Remover that did not leave the page (cancelled, failed): back to "…", after the dialog / menu closed.
    private void OnActionsFocusRequested(object? sender, EventArgs e) =>
        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => MoreActionsButton.Focus(FocusState.Programmatic));

    // Beacon C1 N2: "A atualizar..." is announced when a refresh starts (the text is the live region; it becomes visible
    // with the refresh, so the event is raised after that layout pass).
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ServerDetailViewModel.IsRefreshing) || !ViewModel.IsRefreshing)
        {
            return;
        }

        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            (FrameworkElementAutomationPeer.FromElement(RefreshingText) ?? FrameworkElementAutomationPeer.CreatePeerForElement(RefreshingText))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var returnTarget = ViewModel.TakeReturnFocus(out var pageId);
        // UI.7 B-5: back from the editor without saving ("Voltar ao detalhe", Cancelar, Esc), focus returns to Editar.
        var editorOpener = _editorReturnFocus?.Take(NavigationDestination.Detail);
        if (ShellPageFocus.GetKeepSidebar(this)) return;
        Control? target = returnTarget switch
        {
            ServerDetailReturnTarget.History => HistoryRow,
            ServerDetailReturnTarget.Workloads => WorkloadsRow,
            ServerDetailReturnTarget.ServerPage => _serverPageRows.Find(row => Equals(row.Tag, pageId)),
            _ => editorOpener is null ? null : FindName(editorOpener) as Control
        };

        // Low priority: after the first layout pass, so the element is realized and can take focus.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (target is null || !target.Focus(FocusState.Programmatic)) ShellPageFocus.FocusHeading(this);
            target?.StartBringIntoView();
        });
    }

    private void OnBreadcrumbParentInvoked(object? sender, EventArgs e) => ViewModel.GoBackCommand.Execute(null);
}
