using Microsoft.UI.Xaml;

namespace ServerMonitor.App.Converters;

/// <summary>x:Bind helpers for the UI.4 views: the priority bar's used / remaining parts of a 0–100 track (112:1040).</summary>
public static class OverviewLayout
{
    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Collapses a container whose children are all closed (a collapsed child adds no StackPanel spacing; an
    /// empty visible container does).</summary>
    public static Visibility Any(bool first, bool second) => first || second ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Priority card icon (Figma 112:1033): HardDrive for disk, Computer for CPU / memory (no new icon in UI.4), in the
    /// attention (warning) or danger (critical) text colour. Slot: 0 disk·warning, 1 disk·critical, 2 other·warning,
    /// 3 other·critical — exactly one is visible.
    /// </summary>
    public static Visibility PriorityIcon(ViewModels.PriorityMetric metric, ServerMonitor.Core.Enums.ServerHealth severity, int slot)
    {
        var disk = metric == ViewModels.PriorityMetric.Disk;
        var critical = severity == ServerMonitor.Core.Enums.ServerHealth.Critical;
        return (disk ? 0 : 2) + (critical ? 1 : 0) == slot ? Visibility.Visible : Visibility.Collapsed;
    }

    public static GridLength UsedStar(double percent) => new(Math.Clamp(double.IsNaN(percent) ? 0 : percent, 0, 100), GridUnitType.Star);

    public static GridLength RemainingStar(double percent) => new(100 - Math.Clamp(double.IsNaN(percent) ? 0 : percent, 0, 100), GridUnitType.Star);
}
