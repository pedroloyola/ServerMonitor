using Microsoft.UI.Xaml;

namespace ServerMonitor.App.Services;

public sealed class WindowContext : IWindowContext
{
    private Window? _window;
    private FrameworkElement? _rootElement;

    public XamlRoot XamlRoot => _rootElement?.XamlRoot
        ?? throw new InvalidOperationException("The main window XAML root is not ready.");

    public nint WindowHandle => _window is null
        ? throw new InvalidOperationException("The main window is not ready.")
        : WinRT.Interop.WindowNative.GetWindowHandle(_window);

    public void Attach(Window window, FrameworkElement rootElement)
    {
        _window = window;
        _rootElement = rootElement;
    }
}
