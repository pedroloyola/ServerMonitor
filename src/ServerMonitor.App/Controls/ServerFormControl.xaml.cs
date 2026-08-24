using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Controls;

public sealed partial class ServerFormControl : UserControl
{
    public ServerFormControl()
    {
        InitializeComponent();
    }

    public void FocusFirstField() => NameField.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
}
