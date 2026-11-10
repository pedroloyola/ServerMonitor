using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>
/// "Definições", the General Settings sub-page (UI.5 §4; "Dados e servidores" is <see cref="SettingsDataPage"/>).
/// A singleton: code-behind is view logic only (bringing the Background section into view).
/// </summary>
public sealed partial class SettingsPage : Page, ISettingsNavigationTarget
{
    private readonly List<(RealizedShellLink Link, Button Card, SettingsLinkCardContent Content)> _linkCards = [];
    private ColumnDefinition? _cardColumn;
    private readonly ShellContributionHost _contributions;
    private readonly INavigationService _navigation;

    public SettingsPage(
        SettingsViewModel viewModel,
        WindowModeViewModel windowMode,
        ShellContributionHost contributions,
        INavigationService navigation)
    {
        InitializeComponent();
        ViewModel = viewModel;
        WindowMode = windowMode;
        DataContext = ViewModel;
        _contributions = contributions;
        _navigation = navigation;
        AddContributedSections(contributions);
        AddLinkCards(contributions);
        WidthStates.CurrentStateChanged += (_, args) => PlaceLinkCards(args.NewState?.Name == "Narrow");
        Loaded += (_, _) => PlaceLinkCards(WidthStates.CurrentState?.Name == "Narrow");
        Loaded += OnLoaded;
    }

    /// <summary>
    /// M15.4a r4 (Figma): the composition-supplied Settings cards, between "Dados e servidores" and "Sobre o ServerAlyzer",
    /// drawn by <c>SettingsLinkCardTemplate</c> (glass disclosure, icon, label, the hint visible after it, arrow); the
    /// label is the Narrator's name and the hint the help text. A click opens the card's page through the navigation (its
    /// exit guard included). With none, nothing here touches the grid.
    /// </summary>
    private void AddLinkCards(ShellContributionHost contributions)
    {
        var template = (DataTemplate)Resources["SettingsLinkCardTemplate"];
        foreach (var link in contributions.SettingsCards)
        {
            var content = new SettingsLinkCardContent(link.IconPathData ?? (string)Application.Current.Resources["SaIconPackageData"]);
            var card = new Button
            {
                Style = (Style)Application.Current.Resources["SaDisclosureButtonStyle"],
                Content = content,
                ContentTemplate = template,
            };
            var target = link.TargetId;
            card.Click += (_, _) => _navigation.GoToContribution(target);
            DisclosureGrid.Children.Add(card);
            _linkCards.Add((link, card, content));
        }

        if (_linkCards.Count == 0) return;
        // The third column of the wide row (zero below 700, where the XAML zeroes the second).
        _cardColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        DisclosureGrid.ColumnDefinitions.Add(_cardColumn);
        ApplyLinkCardText();
        Loaded += (_, _) =>
        {
            ApplyLinkCardText();
            _contributions.LinkTextChanged += OnLinkTextChanged;
        };
        Unloaded += (_, _) => _contributions.LinkTextChanged -= OnLinkTextChanged;
    }

    /// <summary>
    /// M15.4a r4: every card's cell for the current width state (SettingsLinkCardLayout): "Sobre" always (the XAML's
    /// Narrow state no longer moves it), the contributed cards, the third column and the rows when there are any.
    /// Without a contributed card this is the XAML's row exactly: "Sobre" at (0,1) wide and (1,0) narrow.
    /// </summary>
    private void PlaceLinkCards(bool narrow)
    {
        var count = _linkCards.Count;
        var (aboutRow, aboutColumn) = SettingsLinkCardLayout.Place(SettingsLinkCardLayout.AboutSequence(count), count, narrow);
        Grid.SetRow(AboutDisclosure, aboutRow);
        Grid.SetColumn(AboutDisclosure, aboutColumn);
        if (count == 0) return;

        _cardColumn!.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        var rows = SettingsLinkCardLayout.Rows(count, narrow);
        while (DisclosureGrid.RowDefinitions.Count < rows) DisclosureGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (DisclosureGrid.RowDefinitions.Count > rows) DisclosureGrid.RowDefinitions.RemoveAt(DisclosureGrid.RowDefinitions.Count - 1);
        // Wide, a second row of cards takes the narrow row gap; a single row keeps the XAML's (none). Narrow, the XAML sets it.
        if (!narrow && SettingsLinkCardLayout.UsedRows(count, narrow) > 1)
            DisclosureGrid.RowSpacing = (double)Application.Current.Resources["SaSpace12"];
        else
            DisclosureGrid.ClearValue(Grid.RowSpacingProperty);
        for (var i = 0; i < count; i++)
        {
            var (row, column) = SettingsLinkCardLayout.Place(SettingsLinkCardLayout.ContributedSequence(i), count, narrow);
            Grid.SetRow(_linkCards[i].Card, row);
            Grid.SetColumn(_linkCards[i].Card, column);
        }
    }

    private void OnLinkTextChanged(object? sender, EventArgs args) => ApplyLinkCardText();

    /// <summary>M15.4a: each card's label and hint, read again through the shell (its try/catch, its fallbacks).</summary>
    private void ApplyLinkCardText()
    {
        foreach (var (link, card, content) in _linkCards)
        {
            var text = _contributions.LabelOf(link);
            var hint = _contributions.HintOf(link);
            content.SetText(text, hint);
            AutomationProperties.SetName(card, text);
            AutomationProperties.SetHelpText(card, hint ?? string.Empty);
        }
    }

    /// <summary>
    /// M15.4a: each contributed section as a titled block like the built-in ones (heading level 2, then the view). The
    /// views are created here, on the UI thread, once (the page is a singleton); a factory that failed was already left
    /// out by the host. Each section is placed through the host's <see cref="ShellContributionHost.TryAttach"/>, so a
    /// view that still fails when it is added leaves out that section only and this page still builds. With none the
    /// host stays collapsed.
    /// </summary>
    private void AddContributedSections(ShellContributionHost contributions)
    {
        foreach (var section in contributions.SettingsSections)
        {
            contributions.TryAttach(section, view =>
            {
                var title = new TextBlock { Text = section.Title, Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["SaCardTitleTextStyle"] };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
                var block = new StackPanel { Spacing = 10 };
                block.Children.Add(title);
                block.Children.Add((Microsoft.UI.Xaml.UIElement)view);
                ContributedSections.Children.Add(block); // last: a block that failed above never reaches the page
            });
        }

        ContributedSections.Visibility = ContributedSections.Children.Count > 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>Backs the compact widget's always-on-top preference toggle.</summary>
    public WindowModeViewModel WindowMode { get; }

    /// <summary>
    /// Cortex #6: a Background request made while this page is ALREADY shown (tray loss / degradation toast with
    /// Settings open) gets no Loaded — the navigation calls this instead, so it is honoured now, not on the next visit.
    /// </summary>
    public void OnNavigatedToAgain() => BringRequestedSectionIntoView();

    /// <summary>Cortex B1 M-1: Loaded has run (the request can be honoured now); before that, Loaded consumes it.</summary>
    public bool IsReadyForSectionRequest => IsLoaded;

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Consume any pending "land on the Background section" request FIRST (M13 S2 §11): it is set by
        // the background notice's activation around the navigation, and it is what makes that activation
        // open on the right section instead of the top of Settings.
        BringRequestedSectionIntoView();
        await ViewModel.LoadAsync();
    }

    private void BringRequestedSectionIntoView()
    {
        ViewModel.NotifyNavigatedTo();
        if (ViewModel.IsBackgroundSectionRequested)
        {
            // M13 S2 §11 / Beacon C1 M4 (same pattern as About): in view AND focused on its toggle, after the layout pass.
            BackgroundSection.StartBringIntoView();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                BackgroundControl.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                BackgroundSection.StartBringIntoView();
            });
        }
        else
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ShellPageFocus.FocusHeading(this));
        }
    }
}
