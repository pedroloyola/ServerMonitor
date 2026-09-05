using System.Drawing;

namespace ServerMonitor.App.Shell.Tray;

/// <summary>The tray interactions we accept. Closed list; anything else is discarded.</summary>
internal enum TrayCallbackAction
{
    /// <summary>Left click or keyboard select: open/restore the window.</summary>
    Open,

    /// <summary>Context menu requested, with a sanitized anchor.</summary>
    ContextMenu
}

/// <summary>A decoded, validated callback. Only produced when every check passed.</summary>
internal readonly record struct TrayCallback(TrayCallbackAction Action, Point Anchor);

/// <summary>
/// The <c>WndProc</c> trust model (CV-1), as a pure function so all seven points are testable without a
/// window.
/// <para>
/// <b>Default-deny.</b> Anything that does not match the contract EXACTLY is discarded and produces no
/// effect. There is no "do the reasonable thing" fallback.
/// </para>
/// <para>
/// The message id is a SELECTOR, never proof of origin: it is guessable by design (<c>WM_APP + n</c>),
/// and that is acceptable only because nothing treats it as a security control. A local process can find
/// the window and send anything; under this contract the ceiling of a forged message is UI nuisance —
/// the window appearing, or a flyout at an anchor of the attacker's choosing. It cannot reach
/// <c>RequestExit</c> without a real click on a real menu item.
/// </para>
/// </summary>
internal static class TrayCallbackContract
{
    /// <summary>Our callback message. WM_APP + 17.</summary>
    internal const uint CallbackMessage = 0x8000 + 17;

    /// <summary>The single icon id. Any other identifier is discarded, even for a listed event.</summary>
    internal const uint IconId = 1;

    // NOTIFYICON_VERSION_4 event codes, as a CLOSED list. Values valid in other protocol versions and
    // values out of range are discarded.
    private const ushort NIN_SELECT = 0x0400;
    private const ushort NIN_KEYSELECT = 0x0401;
    private const ushort WM_CONTEXTMENU = 0x007B;
    private const ushort WM_LBUTTONDBLCLK = 0x0203;

    /// <summary>
    /// Decodes a v4 tray callback, or refuses it.
    /// </summary>
    /// <param name="message">The window message id received.</param>
    /// <param name="wParam">
    /// UNTRUSTED coordinates. Used exclusively as the flyout anchor — never as an index, offset,
    /// dimension, count, buffer size, or any arithmetic input that determines a memory access.
    /// </param>
    /// <param name="lParam">Low word: the event. High word: the icon id.</param>
    /// <param name="isOnScreen">
    /// Whether a point lies inside some monitor's work area. Injected so the sanitisation is testable
    /// without a desktop.
    /// </param>
    /// <returns>The decoded callback, or <c>null</c> when the message is refused.</returns>
    internal static TrayCallback? TryDecode(
        uint message,
        nint wParam,
        nint lParam,
        Func<Point, bool> isOnScreen)
    {
        ArgumentNullException.ThrowIfNull(isOnScreen);

        // (2) Identity of the message. Any other id is not ours.
        if (message != CallbackMessage)
        {
            return null;
        }

        var value = unchecked((uint)lParam.ToInt64());
        var eventCode = (ushort)(value & 0xFFFF);
        var iconId = (uint)((value >> 16) & 0xFFFF);

        // (4) The icon id must be exactly 1, even for an event on the closed list.
        if (iconId != IconId)
        {
            return null;
        }

        // (3) Closed list of v4 events.
        var action = eventCode switch
        {
            NIN_SELECT or NIN_KEYSELECT or WM_LBUTTONDBLCLK => (TrayCallbackAction?)TrayCallbackAction.Open,
            WM_CONTEXTMENU => TrayCallbackAction.ContextMenu,
            _ => null
        };

        if (action is not { } decoded)
        {
            return null;
        }

        // (5) wParam is untrusted. It is an anchor and nothing else, and it is sanitised: a point
        // outside every monitor has no legitimate origin, so the message is DISCARDED rather than
        // corrected. Fail-closed, and trivially testable.
        var anchorValue = unchecked((uint)wParam.ToInt64());
        var anchor = new Point((short)(anchorValue & 0xFFFF), (short)((anchorValue >> 16) & 0xFFFF));

        if (decoded == TrayCallbackAction.ContextMenu && !isOnScreen(anchor))
        {
            return null;
        }

        // (6) Nothing here dereferences a pointer: wParam and lParam are read as integers only.
        return new TrayCallback(decoded, anchor);
    }

    /// <summary>Builds an lParam the way the shell does, for tests and for documentation of the layout.</summary>
    internal static nint EncodeLParam(ushort eventCode, ushort iconId) =>
        (nint)((uint)eventCode | ((uint)iconId << 16));

    /// <summary>Builds a wParam anchor the way the shell does.</summary>
    internal static nint EncodeAnchor(short x, short y) =>
        (nint)(((uint)(ushort)x) | ((uint)(ushort)y << 16));

    internal static ushort SelectEvent => NIN_SELECT;

    internal static ushort ContextMenuEvent => WM_CONTEXTMENU;
}
