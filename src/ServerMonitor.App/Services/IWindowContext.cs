using Microsoft.UI.Xaml;

namespace ServerMonitor.App.Services;

public interface IWindowContext
{
    XamlRoot XamlRoot { get; }

    void Attach(FrameworkElement rootElement);
}
