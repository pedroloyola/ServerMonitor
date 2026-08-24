using Microsoft.UI.Xaml;

namespace ServerMonitor.App.Services;

public interface IWindowContext
{
    XamlRoot XamlRoot { get; }

    nint WindowHandle { get; }

    void Attach(Window window, FrameworkElement rootElement);
}
