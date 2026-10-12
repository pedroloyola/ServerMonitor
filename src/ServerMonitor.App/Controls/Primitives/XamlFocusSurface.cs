using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.11.1 H03: the window's real focus for <see cref="RemountFocusPort"/> - one WinUI call per member, no decision
/// (Atlas review AT-05: the decisions are unit-tested through the port).
/// </summary>
internal sealed class XamlFocusSurface(XamlRoot root, DispatcherQueue dispatcher) : IFocusSurface
{
    public object? FocusedElement => FocusManager.GetFocusedElement(root) as Control;

    public RemountFocusKind KindOf(object element) => ((Control)element).FocusState switch
    {
        FocusState.Pointer => RemountFocusKind.Pointer,
        FocusState.Keyboard => RemountFocusKind.Keyboard,
        _ => RemountFocusKind.Programmatic // Unfocused here = the pre-UI.11.1 behaviour
    };

    public bool CanFocus(object element) => element is Control { XamlRoot: not null };

    public void Focus(object element, RemountFocusKind kind) => ((Control)element).Focus(kind switch
    {
        RemountFocusKind.Pointer => FocusState.Pointer,
        RemountFocusKind.Keyboard => FocusState.Keyboard,
        _ => FocusState.Programmatic
    });

    public bool SharesGroup(object a, object b) =>
        a is DependencyObject first && b is DependencyObject second
        && VisualTreeHelper.GetParent(first) is { } parent && ReferenceEquals(parent, VisualTreeHelper.GetParent(second));

    public void Post(Action action) => dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => action());
}
