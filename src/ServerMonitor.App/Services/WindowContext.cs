using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Services;

public sealed class WindowContext : IWindowContext
{
    private Window? _window;
    private FrameworkElement? _rootElement;
    private Panel? _modalHost;

    public XamlRoot XamlRoot => _rootElement?.XamlRoot
        ?? throw new InvalidOperationException("The main window XAML root is not ready.");

    public nint WindowHandle => _window is null
        ? throw new InvalidOperationException("The main window is not ready.")
        : WinRT.Interop.WindowNative.GetWindowHandle(_window);

    public ElementTheme ActualTheme => _rootElement?.ActualTheme ?? ElementTheme.Default;

    public Panel? ModalHost => _modalHost;

    public void Attach(Window window, FrameworkElement rootElement, Panel? modalHost = null)
    {
        _window = window;
        _rootElement = rootElement;
        _modalHost = modalHost;
    }
}
