using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Services;
using ServerMonitor.App.ViewModels;
namespace ServerMonitor.App.Controls;
public sealed partial class SaSidebar : UserControl
{
    public SaSidebar()
    {
        InitializeComponent();
        SizeChanged += (_, _) => NavGrid.MinHeight = Math.Max(380, ActualHeight);
    }
    public void SetRail(bool rail)
    {
        NavGrid.Padding = (Thickness)Application.Current.Resources[rail ? "SaRailPadding" : "SaSidebarPadding"];
        foreach (var item in new[] { OverviewItem, ServersItem, HistoryItem, SettingsItem }) item.Width = rail ? 48 : 160;
        foreach (var label in new[] { OverviewLabel, ServersLabel, HistoryLabel, SettingsLabel, BrandText }) label.Visibility = rail ? Visibility.Collapsed : Visibility.Visible;
        Brand.HorizontalAlignment = rail ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }
    public bool FocusSelected()
    {
        var item = new[] { OverviewItem, ServersItem, HistoryItem, SettingsItem }.FirstOrDefault(i => i.IsChecked == true);
        return item?.Focus(FocusState.Programmatic) == true;
    }
    private void OnItemClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is ShellViewModel shell && sender is RadioButton { Tag: string destination })
            shell.Navigate(Enum.Parse<ShellDestination>(destination));
    }
}
