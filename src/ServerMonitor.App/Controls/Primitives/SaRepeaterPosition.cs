using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// The "x of N" of a row hosted by an <see cref="ItemsRepeater"/>, over ALL the list's items (not only the realized ones),
/// so a screen reader announces position and total even in a virtualized list of 500. Shared by every row peer
/// (UI.3 <see cref="SaDataTableRowAutomationPeer"/>, UI.4 <see cref="SaListRowAutomationPeer"/> and the Servidores table
/// row). -1 when the element is not inside a repeater (the peer then falls back to its base answer).
/// </summary>
public static class SaRepeaterPosition
{
    public static int PositionInSet(UIElement element) =>
        Repeater(element) is { } repeater && repeater.GetElementIndex(element) is var index and >= 0 ? index + 1 : -1;

    public static int SizeOfSet(UIElement element) =>
        Repeater(element)?.ItemsSourceView?.Count is { } count and > 0 ? count : -1;

    private static ItemsRepeater? Repeater(UIElement element)
    {
        for (var current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ItemsRepeater repeater)
            {
                return repeater;
            }
        }

        return null;
    }
}
