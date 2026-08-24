using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Views;

public sealed partial class RemoveServerDialog : ContentDialog
{
    public RemoveServerDialog(string serverName)
    {
        ServerName = serverName;
        InitializeComponent();
    }

    public string ServerName { get; }
}
