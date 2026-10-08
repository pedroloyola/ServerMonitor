using Microsoft.UI.Xaml;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.8 RC-8 / Prism c1 P-2: where keyboard focus goes when the Compact window opens, and how. Pure, so the window's
/// one-shot layout wait is decided here and tested without WinUI.
/// <list type="bullet">
/// <item>Rows → the first row VISIBLE in the list's viewport (Beacon c2 B-6: re-entering with the list scrolled keeps the
/// user's scroll position, and row 1 is then not realized at all); a state with a real action → that action; otherwise
/// "Expandir".</item>
/// <item>A target that exists but is not laid out yet is WAITED for (the next layout pass), never replaced by the fallback
/// - up to <see cref="MaxLayoutPasses"/> passes (a pass count, not a timer), then "Expandir". Each waiting pass requests the
/// next one (<see cref="CompactEntryFocusWait"/>), so the fallback is always reached even when the layout has settled.</item>
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
        FirstVisibleRow,
        StateAction,
        Expand
    }

    /// <param name="hasRows">The list shows at least one row (the body is the list).</param>
    /// <param name="visibleRowReady">A realized, loaded row is visible in the list's viewport (<see cref="FirstVisibleRow"/>).</param>
    /// <param name="hasAction">The state block offers a real action (Adicionar / Gerir ocultos).</param>
    /// <param name="actionReady">That action button is visible and loaded.</param>
    /// <param name="layoutPasses">Layout passes already waited.</param>
    public static Target Decide(bool hasRows, bool visibleRowReady, bool hasAction, bool actionReady, int layoutPasses)
    {
        if (hasRows)
        {
            return visibleRowReady ? Target.FirstVisibleRow : layoutPasses < MaxLayoutPasses ? Target.Wait : Target.Expand;
        }

        if (hasAction)
        {
            return actionReady ? Target.StateAction : layoutPasses < MaxLayoutPasses ? Target.Wait : Target.Expand;
        }

        return Target.Expand;
    }

    /// <summary>
    /// Beacon c2 B-6: the row to focus among the REALIZED rows (the repeater also realizes a cache outside the viewport):
    /// the first, by index, whose top lies inside the viewport; else the first that overlaps it (a row taller than the
    /// viewport, or a partly scrolled one); null when none is in view yet. Tops are relative to the viewport's top edge.
    /// </summary>
    public static int? FirstVisibleRow(IEnumerable<(int Index, double Top, double Height)> realizedRows, double viewportHeight)
    {
        if (viewportHeight <= 0)
        {
            return null;
        }

        var rows = realizedRows.Where(row => row.Height > 0).OrderBy(row => row.Index).ToList();
        foreach (var row in rows)
        {
            if (row.Top >= -0.5 && row.Top < viewportHeight)
            {
                return row.Index;
            }
        }

        foreach (var row in rows)
        {
            if (row.Top + row.Height > 0 && row.Top < viewportHeight)
            {
                return row.Index;
            }
        }

        return null;
    }

    /// <summary>Keyboard only for a keyboard entry; every other entry is Pointer (focused, no ring).</summary>
    public static FocusState StateFor(bool enteredByKeyboard) => enteredByKeyboard ? FocusState.Keyboard : FocusState.Pointer;
}
