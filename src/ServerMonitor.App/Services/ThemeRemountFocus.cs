namespace ServerMonitor.App.Services;

/// <summary>How the focused element had focus (mirrors Microsoft.UI.Xaml.FocusState without the WinUI type).</summary>
public enum RemountFocusKind
{
    None,
    Pointer,
    Keyboard,
    Programmatic
}

/// <summary>
/// UI.11.1 H03 (Atlas review AT-05): the window's real focus reduced to member accesses with no decision in them -
/// <c>XamlFocusSurface</c> in production (one WinUI call each), a fake in tests.
/// </summary>
public interface IFocusSurface
{
    /// <summary>The focused control, or null.</summary>
    object? FocusedElement { get; }

    RemountFocusKind KindOf(object element);

    /// <summary>The element is a control that is still in a tree (can take focus).</summary>
    bool CanFocus(object element);

    void Focus(object element, RemountFocusKind kind);

    void Post(Action action);
}

/// <summary>
/// The production <see cref="IRemountFocusPort"/>: captures the focused element WITH its focus kind, and restores exactly
/// that captured element - never whatever has focus when the restore runs - when it can still take focus.
/// </summary>
public sealed class RemountFocusPort(IFocusSurface surface) : IRemountFocusPort
{
    public (object? Element, RemountFocusKind Kind) Focused() =>
        surface.FocusedElement is { } element ? (element, surface.KindOf(element)) : (null, RemountFocusKind.None);

    public void Restore(object element, RemountFocusKind kind)
    {
        if (surface.CanFocus(element))
        {
            surface.Focus(element, kind);
        }
    }

    public void Post(Action action) => surface.Post(action);
}

/// <summary>The window's focus, as the theme remount batch needs it (<see cref="RemountFocusPort"/> in production, fake in tests).</summary>
public interface IRemountFocusPort
{
    /// <summary>The focused element and how it got focus; (null, None) when nothing has focus.</summary>
    (object? Element, RemountFocusKind Kind) Focused();

    /// <summary>Gives focus back to <paramref name="element"/> with the same kind (no ring for a pointer focus).</summary>
    void Restore(object element, RemountFocusKind kind);

    /// <summary>Runs <paramref name="action"/> after the remounted content is laid out (Low priority in production).</summary>
    void Post(Action action);
}

/// <summary>
/// UI.11.1 H03 (Beacon S0 §1, Prism S0 U1, Cortex S0 X1). A theme change remounts several roots in ONE pass (the page, the
/// sidebar, first run, compact - MainWindow.OnActualThemeChanged). Each remount used to capture "the focused element" on its
/// own and restore it later; after the FIRST root left the tree, focus had already moved (to the first focusable item -
/// "Sistema" in Settings), so the later captures recorded that and their restores ran last: focus ended on "Sistema", not on
/// the theme just chosen. The batch captures focus ONCE, before any content leaves the tree, and restores it ONCE, after all
/// of them, with the SAME focus kind (a pointer focus stays ring-less, a keyboard focus keeps its ring). Nothing focused,
/// nothing restored: focus is never pulled into a group it was not in.
/// <para>
/// Atlas review A-2 (rounds 2 and 3): the restore runs later (after layout), so it must never override a newer choice.
/// ONE rule, by identity: focus is ours to move only while it sits exactly on the element OUR remount dropped it on (the
/// fallback, recorded per pass), or nowhere, or already on the captured element. Any other element - another item of the
/// same group included (a keyboard choice there is indistinguishable from a fallback by position) - is the user's.
/// A newer pass supersedes a pending restore. It inherits the pending capture only while focus is still on the previous
/// pass's fallback (or nowhere): the user has not chosen anything since, and the fallen item must not become the capture.
/// Otherwise the user's new choice is the capture. Each pass records its own fallback. One instance per window (MainWindow).
/// </para>
/// </summary>
public sealed class ThemeRemountFocus
{
    private int _generation;
    private (object Element, RemountFocusKind Kind, object? Fallback)? _pending;

    /// <returns>How many remounts happened.</returns>
    public int RemountAll(IRemountFocusPort port, IReadOnlyList<Func<bool>> remounts)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(remounts);
        var focused = port.Focused();
        var captured = _pending is { } pending && IsOurs(focused.Element, pending.Fallback, pending.Element)
            ? (pending.Element, pending.Kind)
            : Capture(focused);
        var count = 0;
        foreach (var remount in remounts)
        {
            if (remount())
            {
                count++;
            }
        }

        if (count == 0 || captured is not { } restore)
        {
            return count; // a pending restore (if any) still runs, its generation is still current
        }

        var fallback = port.Focused().Element; // exactly where THIS pass's remounts left focus
        _pending = (restore.Element, restore.Kind, fallback);
        var generation = ++_generation;
        port.Post(() =>
        {
            if (generation != _generation)
            {
                return; // superseded by a newer pass
            }

            _pending = null;
            if (IsOurs(port.Focused().Element, fallback, restore.Element))
            {
                port.Restore(restore.Element, restore.Kind);
            }
        });

        return count;
    }

    /// <summary>Focus is still ours to move: nowhere, exactly on our fallback, or already on the captured element.</summary>
    private static bool IsOurs(object? current, object? fallback, object captured) =>
        current is null || Equals(current, fallback) || Equals(current, captured);

    private static (object Element, RemountFocusKind Kind)? Capture((object? Element, RemountFocusKind Kind) focused) =>
        focused.Element is not null && focused.Kind != RemountFocusKind.None ? (focused.Element, focused.Kind) : null;
}
