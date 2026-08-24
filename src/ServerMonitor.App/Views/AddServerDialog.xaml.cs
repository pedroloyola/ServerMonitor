using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Views;

public sealed partial class AddServerDialog : ContentDialog
{
    public AddServerDialog(ServerEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public ServerEditorViewModel ViewModel { get; }

    public ServerInput? ResultInput { get; private set; }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!ViewModel.TryCreateInput(out var input))
        {
            args.Cancel = true;
            ServerForm.FocusFirstField();
            return;
        }

        ResultInput = input;
    }
}
