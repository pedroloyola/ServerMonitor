using System.Drawing;
using ServerMonitor.App.Shell.Tray;

namespace ServerMonitor.App.Tests.Shell;

/// <summary>
/// CV-1 and CV-6b. The four validation cases are INDEPENDENT: each varies exactly one field and leaves
/// every other field valid, so each assertion isolates a single filter.
/// <para>
/// A conjunctive proof would pass with half the validation removed — if only the message id were
/// checked, a message that is wrong in both fields is still refused by the first filter, and the test
/// stays green over an incomplete contract. That is the defect this shape exists to prevent.
/// </para>
/// </summary>
public sealed class TrayCallbackContractTests
{
    private static readonly Func<Point, bool> AlwaysOnScreen = _ => true;
    private static readonly Func<Point, bool> NeverOnScreen = _ => false;

    private const uint WrongMessage = TrayCallbackContract.CallbackMessage + 1;
    private const ushort ValidEvent = 0x0400;      // NIN_SELECT
    private const ushort UnlistedEvent = 0x0205;   // WM_RBUTTONUP: valid in v3, not on our closed list
    private const uint WrongIconId = 7;

    // ------------------------------------------------------------------ CV-6b: four independent cases

    [Fact]
    public void A_valid_callback_and_a_valid_uid_are_accepted()
    {
        var result = TrayCallbackContract.TryDecode(
            TrayCallbackContract.CallbackMessage,
            TrayCallbackContract.EncodeAnchor(10, 10),
            TrayCallbackContract.EncodeLParam(ValidEvent, (ushort)TrayCallbackContract.IconId),
            AlwaysOnScreen);

        Assert.NotNull(result);
        Assert.Equal(TrayCallbackAction.Open, result!.Value.Action);
    }

    [Fact]
    public void B_an_invalid_callback_id_with_an_otherwise_valid_message_is_ignored()
    {
        // Everything else is VALID. This isolates the message-identity filter.
        var result = TrayCallbackContract.TryDecode(
            WrongMessage,
            TrayCallbackContract.EncodeAnchor(10, 10),
            TrayCallbackContract.EncodeLParam(ValidEvent, (ushort)TrayCallbackContract.IconId),
            AlwaysOnScreen);

        Assert.Null(result);
    }

    [Fact]
    public void C_a_valid_callback_id_with_an_invalid_uid_is_ignored()
    {
        // Everything else is VALID. This isolates the icon-id filter — the half a conjunctive test
        // would never exercise.
        var result = TrayCallbackContract.TryDecode(
            TrayCallbackContract.CallbackMessage,
            TrayCallbackContract.EncodeAnchor(10, 10),
            TrayCallbackContract.EncodeLParam(ValidEvent, (ushort)WrongIconId),
            AlwaysOnScreen);

        Assert.Null(result);
    }

    [Fact]
    public void D_an_invalid_callback_id_and_an_invalid_uid_is_ignored()
    {
        var result = TrayCallbackContract.TryDecode(
            WrongMessage,
            TrayCallbackContract.EncodeAnchor(10, 10),
            TrayCallbackContract.EncodeLParam(ValidEvent, (ushort)WrongIconId),
            AlwaysOnScreen);

        Assert.Null(result);
    }

    // ------------------------------------------------------------------ CV-1 points 3, 5, 6

    [Fact]
    public void An_event_outside_the_closed_v4_list_is_discarded()
    {
        // A v3-valid event with a correct id and a correct uid. The closed list is the control.
        var result = TrayCallbackContract.TryDecode(
            TrayCallbackContract.CallbackMessage,
            TrayCallbackContract.EncodeAnchor(10, 10),
            TrayCallbackContract.EncodeLParam(UnlistedEvent, (ushort)TrayCallbackContract.IconId),
            AlwaysOnScreen);

        Assert.Null(result);
    }

    [Fact]
    public void An_event_value_out_of_range_is_discarded()
    {
        var result = TrayCallbackContract.TryDecode(
            TrayCallbackContract.CallbackMessage,
            TrayCallbackContract.EncodeAnchor(10, 10),
            TrayCallbackContract.EncodeLParam(0xFFFF, (ushort)TrayCallbackContract.IconId),
            AlwaysOnScreen);

        Assert.Null(result);
    }

    [Fact]
    public void An_anchor_outside_every_monitor_is_discarded_rather_than_corrected()
    {
        // The implementer choice CV-1 point 5 leaves open, made explicit and asserted: DISCARD. A point
        // outside every monitor has no legitimate origin, so fail-closed is both correct and simpler to
        // prove than clamping.
        var result = TrayCallbackContract.TryDecode(
            TrayCallbackContract.CallbackMessage,
            TrayCallbackContract.EncodeAnchor(30000, 30000),
            TrayCallbackContract.EncodeLParam(
                TrayCallbackContract.ContextMenuEvent, (ushort)TrayCallbackContract.IconId),
            NeverOnScreen);

        Assert.Null(result);
    }

    [Fact]
    public void A_context_menu_anchor_on_screen_is_accepted_and_carried_through_unchanged()
    {
        var result = TrayCallbackContract.TryDecode(
            TrayCallbackContract.CallbackMessage,
            TrayCallbackContract.EncodeAnchor(1700, 1050),
            TrayCallbackContract.EncodeLParam(
                TrayCallbackContract.ContextMenuEvent, (ushort)TrayCallbackContract.IconId),
            AlwaysOnScreen);

        Assert.NotNull(result);
        Assert.Equal(TrayCallbackAction.ContextMenu, result!.Value.Action);
        Assert.Equal(new Point(1700, 1050), result.Value.Anchor);
    }

    [Fact]
    public void An_open_action_is_not_gated_on_the_anchor()
    {
        // Only the flyout uses the anchor, so a select must not be refused because the coordinates are
        // odd — that would be a fail-closed rule applied where it buys nothing and costs usability.
        var result = TrayCallbackContract.TryDecode(
            TrayCallbackContract.CallbackMessage,
            TrayCallbackContract.EncodeAnchor(30000, 30000),
            TrayCallbackContract.EncodeLParam(
                TrayCallbackContract.SelectEvent, (ushort)TrayCallbackContract.IconId),
            NeverOnScreen);

        Assert.NotNull(result);
        Assert.Equal(TrayCallbackAction.Open, result!.Value.Action);
    }
}
