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
    /// Priority card icon (Figma 112:1033; UI.10 F19, Manual A3 238:250/253): one icon per metric - HardDrive for disk,
    /// Cpu for CPU, RamMemory for memory (Computer means "system"/"compact" elsewhere) - in the attention (warning) or
    /// danger (critical) text colour. Slot: 0/1 disk, 2/3 CPU, 4/5 memory (warning/critical) — exactly one is visible.
    /// </summary>
    public static Visibility PriorityIcon(ViewModels.PriorityMetric metric, ServerMonitor.Core.Enums.ServerHealth severity, int slot)
    {
        var first = metric switch
        {
            ViewModels.PriorityMetric.Disk => 0,
            ViewModels.PriorityMetric.Cpu => 2,
            _ => 4
        };
        var critical = severity == ServerMonitor.Core.Enums.ServerHealth.Critical;
        return first + (critical ? 1 : 0) == slot ? Visibility.Visible : Visibility.Collapsed;
    }

    public static GridLength UsedStar(double percent) => new(Math.Clamp(double.IsNaN(percent) ? 0 : percent, 0, 100), GridUnitType.Star);

    public static GridLength RemainingStar(double percent) => new(100 - Math.Clamp(double.IsNaN(percent) ? 0 : percent, 0, 100), GridUnitType.Star);
}
