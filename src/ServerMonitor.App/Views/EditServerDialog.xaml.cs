using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Views;

public sealed partial class EditServerDialog : ContentDialog
{
    public EditServerDialog(ServerEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public ServerEditorViewModel ViewModel { get; }

    public ServerEditorResult? Result { get; private set; }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (ViewModel.IsTestingConnection)
        {
            args.Cancel = true;
            return;
        }

        ServerForm.CaptureSecret();
        if (!ViewModel.TryCreateResult(out var result))
        {
            args.Cancel = true;
            ServerForm.FocusFirstField();
            return;
        }

        Result = result;
    }
}
