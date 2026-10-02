using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.3 (Beacon L1): an action button that disappears once its command ran (an empty-state CTA) would drop keyboard focus
/// back to the top of the page. Focus moves instead to the element the action changed, with the same kind of focus the
/// button had (keyboard -> visible ring; pointer / UI Automation -> programmatic, no ring), after the layout settles.
/// </summary>
internal static class FocusAfterAction
{
    internal static FocusState FocusStateFor(FocusState sourceState) =>
        sourceState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;

    public static void MoveTo(Control source, Control target)
    {
        var state = FocusStateFor(source.FocusState);
        source.DispatcherQueue.TryEnqueue(() => target.Focus(state));
    }
}
