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

    /// <summary>Both elements are children of the same panel (the items of one group).</summary>
    bool SharesGroup(object a, object b);

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

    public bool SharesGroup(object a, object b) => surface.SharesGroup(a, b);

    public void Post(Action action) => surface.Post(action);
}

/// <summary>The window's focus, as the theme remount batch needs it (<see cref="RemountFocusPort"/> in production, fake in tests).</summary>
public interface IRemountFocusPort
{
    /// <summary>The focused element and how it got focus; (null, None) when nothing has focus.</summary>
    (object? Element, RemountFocusKind Kind) Focused();

    /// <summary>Gives focus back to <paramref name="element"/> with the same kind (no ring for a pointer focus).</summary>
    void Restore(object element, RemountFocusKind kind);

    /// <summary>Both elements are items of one group (focus that fell from one to the other during a remount).</summary>
    bool SharesGroup(object a, object b);

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
/// Atlas review A-2: the restore runs later (after layout), so it must never steal a newer choice. It restores only when
/// focus is still where the remounts left it, nowhere, on the captured element, or on another item of the captured
/// element's group (where the remount dropped it). A newer pass supersedes a pending restore and inherits its capture -
/// after the first pass focus already sits on the fallen item, so the second pass must not capture that.
/// One instance per window (MainWindow).
/// </para>
/// </summary>
public sealed class ThemeRemountFocus
{
    private int _generation;
    private (object Element, RemountFocusKind Kind)? _pending;

    /// <returns>How many remounts happened.</returns>
    public int RemountAll(IRemountFocusPort port, IReadOnlyList<Func<bool>> remounts)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(remounts);
        var captured = _pending ?? Capture(port);
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

        _pending = restore;
        var generation = ++_generation;
        var left = port.Focused().Element;
        port.Post(() =>
        {
            if (generation != _generation)
            {
                return; // superseded by a newer pass, which carries the same capture
            }

            _pending = null;
            var current = port.Focused().Element;
            if (current is null || Equals(current, left) || Equals(current, restore.Element) || port.SharesGroup(current, restore.Element))
            {
                port.Restore(restore.Element, restore.Kind);
            }
        });

        return count;
    }

    private static (object Element, RemountFocusKind Kind)? Capture(IRemountFocusPort port)
    {
        var (element, kind) = port.Focused();
        return element is not null && kind != RemountFocusKind.None ? (element, kind) : null;
    }
}
