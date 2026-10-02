using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.4 (Beacon r1 SHOULD-1/5): puts keyboard focus on the row of a virtualized <see cref="ItemsRepeater"/> at an index
/// (realizing it first) after the layout settles, so focus returns to the row the user left from instead of falling to
/// the first focusable element of the window (the "Modo compacto" button).
/// </summary>
internal static class RepeaterFocus
{
    public static void FocusIndexLater(ItemsRepeater repeater, int index, FocusState state = FocusState.Programmatic)
    {
        if (index < 0)
        {
            return;
        }

        repeater.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => FocusIndex(repeater, index, state));
    }

    public static bool FocusIndex(ItemsRepeater repeater, int index, FocusState state = FocusState.Programmatic)
    {
        if (index < 0 || repeater.ItemsSourceView is not { } items || index >= items.Count)
        {
            return false;
        }

        if (repeater.GetOrCreateElement(index) is not Control row)
        {
            return false;
        }

        row.StartBringIntoView();
        return row.Focus(state);
    }
}
