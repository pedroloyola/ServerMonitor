using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace ServerMonitor.App.Services;

/// <summary>UI.7 B-5: the name of the control that has keyboard focus now (the editor's opener), for its return slot.</summary>
internal static class FocusOrigin
{
    /// <summary>A capture over the window: null without a window, without focus or for an unnamed element.</summary>
    public static Func<string?> CaptureName(IWindowContext windowContext) => () =>
        FocusManager.GetFocusedElement(windowContext.XamlRoot) is FrameworkElement { Name.Length: > 0 } element
            ? element.Name
            : null;
}
