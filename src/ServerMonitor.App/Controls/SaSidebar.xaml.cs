using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
using Windows.System;
namespace ServerMonitor.App.Controls;
public sealed partial class SaSidebar : UserControl
{
    private readonly SidebarSelectionSync _selection;
    private readonly List<(string Id, RadioButton Item, TextBlock Label)> _contributed = [];
    private ShellViewModel? _contributionShell;
    private bool _contributedBuilt;
    private bool _rail;
    private Button? _footerLink;
    private TextBlock? _footerLabel;
    public SaSidebar()
    {
        InitializeComponent();
        _selection = new SidebarSelectionSync((destination, selected) =>
            Items.Single(item => (string)item.Tag == destination.ToString()).IsChecked = selected);
        Loaded += (_, _) => { AddContributedItems(DataContext as ShellViewModel); _selection.Bind(DataContext as ShellViewModel); BindContributions(DataContext as ShellViewModel); };
        Unloaded += (_, _) => { _selection.Dispose(); BindContributions(null); };
        DataContextChanged += (_, _) => { AddContributedItems(DataContext as ShellViewModel); _selection.Bind(DataContext as ShellViewModel); BindContributions(DataContext as ShellViewModel); };
        SetRail(false);
        SizeChanged += (_, _) => NavGrid.MinHeight = Math.Max(380, ActualHeight);
    }
    private RadioButton[] Items => [OverviewItem, ServersItem, HistoryItem, SettingsItem];
    private RadioButton[] AllItems => [OverviewItem, ServersItem, HistoryItem, .. _contributed.Select(entry => entry.Item), SettingsItem];
    public void SetRail(bool rail)
    {
        _rail = rail;
        NavGrid.Padding = (Thickness)Application.Current.Resources[rail ? "SaRailPadding" : "SaSidebarPadding"];
        foreach (var item in AllItems)
        {
            // UI.10 F46: the rail item is a true circle (46x46, radius 23), the icon centred (13 + 20 + 13).
            item.Width = rail ? ShellLayout.RailItemWidth : ShellLayout.ItemWidth;
            item.Padding = new Thickness(rail ? 13 : 12, 0, rail ? 13 : 12, 0);
            ToolTipService.SetToolTip(item, rail ? Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(item) : null);
            if (item.Content is Grid grid) grid.ColumnSpacing = rail ? 0 : 10;
        }
        if (_footerLink is not null && _footerLabel is not null)
        {
            // M15.4a: the footer link has the items' geometry (176 expanded, the 46 circle in the rail).
            _footerLink.Width = rail ? ShellLayout.RailItemWidth : ShellLayout.ItemWidth;
            _footerLink.Padding = new Thickness(rail ? 13 : 12, 0, rail ? 13 : 12, 0);
            if (_footerLink.Content is Grid footerGrid) footerGrid.ColumnSpacing = rail ? 0 : 10;
            _footerLabel.Visibility = rail ? Visibility.Collapsed : Visibility.Visible;
            ApplyFooterText();
        }
        foreach (var label in new[] { OverviewLabel, ServersLabel, HistoryLabel, SettingsLabel, BrandText }.Concat(_contributed.Select(entry => entry.Label))) label.Visibility = rail ? Visibility.Collapsed : Visibility.Visible;
        Brand.HorizontalAlignment = rail ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }
    public bool FocusSelected()
    {
        var item = AllItems.FirstOrDefault(i => i.IsChecked == true);
        return item?.Focus(FocusState.Programmatic) == true;
    }
    private void OnItemKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is RadioButton { Tag: string destination })
            args.Handled = _selection.ActivateKey(args.Key, Enum.Parse<ShellDestination>(destination));
    }
    private void OnItemClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is ShellViewModel shell && sender is RadioButton { Tag: string destination })
            shell.Navigate(Enum.Parse<ShellDestination>(destination));
        _selection.Refresh();
        RefreshContributed();
    }

    /// <summary>
    /// M15.4a: one item per contributed navigation entry, built once, in the shell's order. With none, nothing here
    /// changes the tree and the sidebar is exactly its XAML. The items are added before the first Loaded, so the sliding
    /// selection indicator collects them together with the built-in ones.
    /// </summary>
    private void AddContributedItems(ShellViewModel? shell)
    {
        if (_contributedBuilt || shell is null) return;
        _contributedBuilt = true;
        AddFooterLink(shell);
        var entries = shell.ContributedEntries;
        if (entries.Count == 0) return;

        // Rows and positions: SidebarContributionLayout (brand, destinations, then the contributed rows before the
        // flexible spacer, so Settings stays at the bottom; the sliding indicator keeps spanning every row).
        var count = entries.Count;
        var size = SidebarContributionLayout.SizeOfSet(count);
        foreach (var child in NavGrid.Children.OfType<FrameworkElement>())
        {
            var row = Grid.GetRow(child);
            Grid.SetRowSpan(child, SidebarContributionLayout.RowSpanAfterInsert(row, Grid.GetRowSpan(child), count));
            Grid.SetRow(child, SidebarContributionLayout.RowAfterInsert(row, count));
        }
        for (var i = 0; i < count; i++)
            NavGrid.RowDefinitions.Insert(SidebarContributionLayout.RowOfContributed(i), new RowDefinition { Height = new GridLength(SidebarContributionLayout.ContributedRowHeight) });

        var settingsIndex = NavGrid.Children.IndexOf(SettingsItem);
        for (var i = 0; i < count; i++)
        {
            var entry = entries[i];
            var label = new TextBlock { Text = entry.Title, MaxWidth = ShellLayout.ItemLabelMaxWidth, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            var content = new Grid { ColumnSpacing = 10 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(new SaIcon { Data = entry.IconPathData ?? (string)Application.Current.Resources["SaIconPackageData"], Size = 20 });
            content.Children.Add(label);
            var item = new RadioButton
            {
                GroupName = "ShellDestinations",
                Style = (Style)Application.Current.Resources["SaNavItemStyle"],
                Height = 46,
                Tag = entry.Id,
                Content = content,
            };
            AutomationProperties.SetName(item, entry.Title);
            AutomationProperties.SetPositionInSet(item, SidebarContributionLayout.PositionOfContributed(i));
            AutomationProperties.SetSizeOfSet(item, size);
            Grid.SetRow(item, SidebarContributionLayout.RowOfContributed(i));
            item.Click += OnContributedItemClick;
            item.KeyDown += OnContributedItemKeyDown;
            NavGrid.Children.Insert(settingsIndex + i, item);
            _contributed.Add((entry.Id, item, label));
        }

        foreach (var builtIn in Items) AutomationProperties.SetSizeOfSet(builtIn, size);
        AutomationProperties.SetPositionInSet(SettingsItem, size);
        SetRail(_rail);
    }

    /// <summary>
    /// M15.4a: the composition-supplied link at the foot of the navigation, just above Settings: a host-drawn button (a
    /// link, not one of the destinations), with the items' geometry, keyboard and focus visuals, its label as the
    /// Narrator's name and its hint as the help text; in the rail, the icon and a tooltip. Built once; without a footer
    /// link nothing here changes the tree.
    /// </summary>
    private void AddFooterLink(ShellViewModel shell)
    {
        if (shell.FooterLink is not { } link) return;
        foreach (var child in NavGrid.Children.OfType<FrameworkElement>())
        {
            var row = Grid.GetRow(child);
            Grid.SetRowSpan(child, SidebarContributionLayout.RowSpanAfterFooter(row, Grid.GetRowSpan(child)));
            Grid.SetRow(child, SidebarContributionLayout.RowAfterFooter(row));
        }
        NavGrid.RowDefinitions.Insert(SidebarContributionLayout.FooterRow, new RowDefinition { Height = new GridLength(SidebarContributionLayout.ContributedRowHeight) });

        _footerLabel = new TextBlock { MaxWidth = ShellLayout.ItemLabelMaxWidth, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_footerLabel, 1);
        var content = new Grid { ColumnSpacing = 10 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(new SaIcon { Data = link.IconPathData ?? (string)Application.Current.Resources["SaIconPackageData"], Size = 20 });
        content.Children.Add(_footerLabel);
        _footerLink = new Button
        {
            Style = (Style)Application.Current.Resources["SaNavFooterLinkStyle"],
            Height = SidebarContributionLayout.ContributedRowHeight,
            Content = content,
        };
        Grid.SetRow(_footerLink, SidebarContributionLayout.FooterRow);
        _footerLink.Click += OnFooterLinkClick;
        NavGrid.Children.Insert(NavGrid.Children.IndexOf(SettingsItem), _footerLink);
        ApplyFooterText();
    }

    private void OnFooterLinkClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is ShellViewModel shell) shell.OpenFooterLink();
        _selection.Refresh();
        RefreshContributed();
    }

    /// <summary>
    /// M15.4a: the footer link's label and hint, read again (built, each navigation, window activation, theme change).
    /// The shell reads the providers inside its own try/catch, with the title resource as the fallback.
    /// </summary>
    private void ApplyFooterText()
    {
        if (_footerLink is null || _footerLabel is null || DataContext is not ShellViewModel shell) return;
        var label = shell.FooterLinkLabel ?? string.Empty;
        var hint = shell.FooterLinkHint;
        _footerLabel.Text = label;
        AutomationProperties.SetName(_footerLink, label);
        AutomationProperties.SetHelpText(_footerLink, hint ?? string.Empty);
        ToolTipService.SetToolTip(_footerLink, _rail ? label : hint);
    }

    private void BindContributions(ShellViewModel? shell)
    {
        if (_contributionShell is not null) _contributionShell.PropertyChanged -= OnShellChanged;
        _contributionShell = _contributed.Count == 0 && _footerLink is null ? null : shell;
        if (_contributionShell is not null) _contributionShell.PropertyChanged += OnShellChanged;
        RefreshContributed();
        ApplyFooterText();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs args)
    {
        RefreshContributed();
        if (args.PropertyName is nameof(ShellViewModel.FooterLinkLabel) or nameof(ShellViewModel.FooterLinkHint)) ApplyFooterText();
    }

    private void RefreshContributed()
    {
        var selected = (DataContext as ShellViewModel)?.SelectedContributionId;
        foreach (var (id, item, _) in _contributed) item.IsChecked = string.Equals(id, selected, StringComparison.Ordinal);
    }

    private void OnContributedItemClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is ShellViewModel shell && sender is RadioButton { Tag: string id }) shell.NavigateToContribution(id);
        _selection.Refresh();
        RefreshContributed();
    }

    private void OnContributedItemKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || DataContext is not ShellViewModel shell || sender is not RadioButton { Tag: string id }) return;
        shell.NavigateToContribution(id);
        _selection.Refresh();
        RefreshContributed();
        args.Handled = true;
    }
}
