using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>UI.11.1 H03: the window's real focus for <see cref="ThemeRemountFocus"/> (WinUI side).</summary>
internal sealed class XamlRemountFocusPort(XamlRoot root, DispatcherQueue dispatcher) : IRemountFocusPort
{
    public (object? Element, RemountFocusKind Kind) Focused() =>
        FocusManager.GetFocusedElement(root) is Control control
            ? (control, control.FocusState switch
            {
                FocusState.Pointer => RemountFocusKind.Pointer,
                FocusState.Keyboard => RemountFocusKind.Keyboard,
                _ => RemountFocusKind.Programmatic // Unfocused here = the pre-UI.11.1 behaviour
            })
            : (null, RemountFocusKind.None);

    public void Restore(object element, RemountFocusKind kind)
    {
        if (element is Control { XamlRoot: not null } control)
        {
            control.Focus(kind switch
            {
                RemountFocusKind.Pointer => FocusState.Pointer,
                RemountFocusKind.Keyboard => FocusState.Keyboard,
                _ => FocusState.Programmatic
            });
        }
    }

    public void Post(Action action) => dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => action());
}
