using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ServerMonitor.App.Views;
namespace ServerMonitor.App.Services;

/// <summary>One-shot return captured before the modal takes focus; detached origins fall back to the page heading.</summary>
internal sealed class DialogReturnFocus(Func<bool> restoreOrigin, Action restoreHeading) : IDisposable
{
    private bool _returned;
    public void Dispose()
    {
        if (_returned) return;
        _returned = true;
        if (!restoreOrigin()) restoreHeading();
    }
    public static Action Capture(XamlRoot? root)
    {
        var origin = root is null ? null : FocusManager.GetFocusedElement(root) as Control;
        var content = root?.Content as FrameworkElement;
        var returnFocus = new DialogReturnFocus(
            () => origin is { IsLoaded: true } && ReferenceEquals(origin.XamlRoot, root) && origin.Focus(FocusState.Programmatic),
            () =>
            {
                if (content?.FindName("ContentFrame") is Frame { Content: Page page }) ShellPageFocus.FocusHeading(page, force: true);
            });
        return () =>
        {
            if (content?.DispatcherQueue is { } queue)
                queue.TryEnqueue(DispatcherQueuePriority.Low, returnFocus.Dispose);
        };
    }
}
