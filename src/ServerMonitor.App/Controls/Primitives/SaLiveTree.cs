using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ServerMonitor.App.Controls.Primitives;

/// <summary>
/// UI.11C (measured at runtime): after a SaThemeRefresh remount WinUI can raise the new Loaded BEFORE the stale Unloaded, so
/// FrameworkElement.IsLoaded may stay false for an element that is on screen. The motion behaviours therefore ask whether the
/// element is actually connected to its window's live tree: its ancestor chain reaches the XamlRoot's content.
/// </summary>
internal static class SaLiveTree
{
    public static bool IsLive(FrameworkElement element)
    {
        var root = element.XamlRoot?.Content;
        if (root is null)
        {
            return false;
        }

        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }
}
