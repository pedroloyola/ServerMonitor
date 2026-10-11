namespace ServerMonitor.App.Services;

/// <summary>How the focused element had focus (mirrors Microsoft.UI.Xaml.FocusState without the WinUI type).</summary>
public enum RemountFocusKind
{
    None,
    Pointer,
    Keyboard,
    Programmatic
}

/// <summary>The window's focus, as the theme remount batch needs it (WinUI adapter in production, fake in tests).</summary>
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
/// </summary>
public static class ThemeRemountFocus
{
    /// <returns>How many remounts happened.</returns>
    public static int RemountAll(IRemountFocusPort port, IReadOnlyList<Func<bool>> remounts)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(remounts);
        var (element, kind) = port.Focused();
        var count = 0;
        foreach (var remount in remounts)
        {
            if (remount())
            {
                count++;
            }
        }

        if (count > 0 && element is not null && kind != RemountFocusKind.None)
        {
            port.Post(() => port.Restore(element, kind));
        }

        return count;
    }
}
