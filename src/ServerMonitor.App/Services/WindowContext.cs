using Microsoft.UI.Xaml;

namespace ServerMonitor.App.Services;

public sealed class WindowContext : IWindowContext
{
    private FrameworkElement? _rootElement;

    public XamlRoot XamlRoot => _rootElement?.XamlRoot
        ?? throw new InvalidOperationException("The main window XAML root is not ready.");

    public void Attach(FrameworkElement rootElement) => _rootElement = rootElement;
}
