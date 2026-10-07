using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.7 B-5: puts keyboard focus back on the control that opened the editor, by its page-level name. A control that is gone,
/// hidden, disabled or templated (no page-level name) returns false and the page falls back to its own rule (H1).
/// </summary>
internal static class EditorReturnFocus
{
    /// <summary>Queued after the first layout pass (the element must be realized to take focus).</summary>
    public static bool TryFocus(Page page, string? elementName)
    {
        if (string.IsNullOrWhiteSpace(elementName) || page.FindName(elementName) is not Control { Visibility: Visibility.Visible })
        {
            return false;
        }

        page.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!TryFocusNow(page, elementName))
            {
                ShellPageFocus.FocusHeading(page);
            }
        });
        return true;
    }

    public static bool TryFocusNow(Page page, string? elementName) =>
        !string.IsNullOrWhiteSpace(elementName)
        && page.FindName(elementName) is Control { Visibility: Visibility.Visible, IsEnabled: true } control
        && control.Focus(FocusState.Programmatic);
}
