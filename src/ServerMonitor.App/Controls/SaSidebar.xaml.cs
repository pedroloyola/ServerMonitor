using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
namespace ServerMonitor.App.Controls;
public sealed partial class SaSidebar : UserControl
{
    private readonly SidebarSelectionSync _selection;
    public SaSidebar()
    {
        InitializeComponent();
        _selection = new SidebarSelectionSync((destination, selected) =>
            Items.Single(item => (string)item.Tag == destination.ToString()).IsChecked = selected);
        Loaded += (_, _) => _selection.Bind(DataContext as ShellViewModel);
        Unloaded += (_, _) => _selection.Dispose();
        DataContextChanged += (_, _) => _selection.Bind(DataContext as ShellViewModel);
        SetRail(false);
        SizeChanged += (_, _) => NavGrid.MinHeight = Math.Max(380, ActualHeight);
    }
    private RadioButton[] Items => [OverviewItem, ServersItem, HistoryItem, SettingsItem];
    public void SetRail(bool rail)
    {
        NavGrid.Padding = (Thickness)Application.Current.Resources[rail ? "SaRailPadding" : "SaSidebarPadding"];
        foreach (var item in new[] { OverviewItem, ServersItem, HistoryItem, SettingsItem })
        {
            item.Width = rail ? 48 : 160;
            item.Padding = new Thickness(rail ? 14 : 12, 0, rail ? 14 : 12, 0);
            ToolTipService.SetToolTip(item, rail ? Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(item) : null);
            if (item.Content is Grid grid) grid.ColumnSpacing = rail ? 0 : 10;
        }
        foreach (var label in new[] { OverviewLabel, ServersLabel, HistoryLabel, SettingsLabel, BrandText }) label.Visibility = rail ? Visibility.Collapsed : Visibility.Visible;
        Brand.HorizontalAlignment = rail ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }
    public bool FocusSelected()
    {
        var item = new[] { OverviewItem, ServersItem, HistoryItem, SettingsItem }.FirstOrDefault(i => i.IsChecked == true);
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
    }
}
