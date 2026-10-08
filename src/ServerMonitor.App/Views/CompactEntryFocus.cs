using Microsoft.UI.Xaml;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.8 RC-8 / Prism c1 P-2: where keyboard focus goes when the Compact window opens, and how. Pure, so the window's
/// one-shot layout wait is decided here and tested without WinUI.
/// <list type="bullet">
/// <item>Rows → the first row; a state with a real action → that action; otherwise "Expandir".</item>
/// <item>A target that exists but is not laid out yet is WAITED for (the next layout pass), never replaced by the fallback
/// - up to <see cref="MaxLayoutPasses"/> passes (a pass count, not a timer), then "Expandir".</item>
/// <item>The focus state is Keyboard only when the user reached Compact with the keyboard; otherwise Pointer, so no focus
/// ring is painted after a click, a tray command or a launch. MEASURED (c1, 6673ec9+f0ce41b): Programmatic still drew the
/// keyboard ring at launch (WinUI keeps the last input mode, keyboard by default), so Pointer is the state that matches
/// "focused, no ring" - the element still owns focus and Tab continues from it.</item>
/// </list>
/// </summary>
internal static class CompactEntryFocus
{
    public const int MaxLayoutPasses = 8;

    public enum Target
    {
        Wait,
        FirstRow,
        StateAction,
        Expand
    }

    /// <param name="hasRows">The list shows at least one row (the body is the list).</param>
    /// <param name="firstRowReady">The first row element exists and is loaded.</param>
    /// <param name="hasAction">The state block offers a real action (Adicionar / Gerir ocultos).</param>
    /// <param name="actionReady">That action button is visible and loaded.</param>
    /// <param name="layoutPasses">Layout passes already waited.</param>
    public static Target Decide(bool hasRows, bool firstRowReady, bool hasAction, bool actionReady, int layoutPasses)
    {
        if (hasRows)
        {
            return firstRowReady ? Target.FirstRow : layoutPasses < MaxLayoutPasses ? Target.Wait : Target.Expand;
        }

        if (hasAction)
        {
            return actionReady ? Target.StateAction : layoutPasses < MaxLayoutPasses ? Target.Wait : Target.Expand;
        }

        return Target.Expand;
    }

    /// <summary>Keyboard only for a keyboard entry; every other entry is Pointer (focused, no ring).</summary>
    public static FocusState StateFor(bool enteredByKeyboard) => enteredByKeyboard ? FocusState.Keyboard : FocusState.Pointer;
}
