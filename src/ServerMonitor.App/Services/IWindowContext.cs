using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Services;

public interface IWindowContext
{
    XamlRoot XamlRoot { get; }

    nint WindowHandle { get; }

    ElementTheme ActualTheme { get; }

    Panel? ModalHost { get; }

    void Attach(Window window, FrameworkElement rootElement, Panel? modalHost = null);
}
