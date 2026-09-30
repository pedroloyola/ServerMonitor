using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

/// <summary>
/// The destructive restore confirm (M14.6 contract §5). The default button is Cancel, so Enter never
/// confirms; only an explicit activation of the primary button does.
/// </summary>
public sealed partial class RestoreConfirmDialog : ContentDialog
{
    public RestoreConfirmDialog(RestoreConfirmation confirmation)
    {
        Confirmation = confirmation;
        InitializeComponent();
        Title = confirmation.Title;
        PrimaryButtonText = confirmation.PrimaryText;
        CloseButtonText = confirmation.CancelText;
    }

    public RestoreConfirmation Confirmation { get; }
}
