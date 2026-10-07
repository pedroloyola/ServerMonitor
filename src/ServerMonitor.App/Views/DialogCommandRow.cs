using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ServerMonitor.App.Views;

/// <summary>
/// UI.7 final c2 (Prism C2-1): the two commands of an in-page dialog (test, trust, import). Side by side they are
/// [safe] [default], right-aligned; when the dialog's content is narrower than both need (the 560 minimum window) they
/// stack full width with the default action first - never a command cut off at the content edge.
/// </summary>
internal static class DialogCommandRow
{
    public static void Apply(StackPanel row, Button safe, Button primary, bool stacked)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.Orientation = stacked ? Orientation.Vertical : Orientation.Horizontal;
        row.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        safe.HorizontalAlignment = primary.HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        var first = Order(safe, primary, stacked).First;
        if (row.Children.IndexOf(first) > 0)
        {
            row.Children.Move((uint)row.Children.IndexOf(first), 0);
        }
    }

    /// <summary>The visual (and Tab) order: [safe, default] side by side, [default, safe] stacked.</summary>
    internal static (T First, T Second) Order<T>(T safe, T primary, bool stacked) => stacked ? (primary, safe) : (safe, primary);
}
