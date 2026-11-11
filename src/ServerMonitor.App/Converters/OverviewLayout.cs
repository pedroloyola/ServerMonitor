using Microsoft.UI.Xaml;
using ServerMonitor.App.ViewModels;

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

    /// <summary>FIX-OVERVIEW-FIT D-1: the health row's column gap (SaSpace8, Figma 112:1014).</summary>
    public const double HealthRowGap = 8;

    /// <summary>FIX-OVERVIEW-FIT D-1: the spacer's minimum (SaSpace24; Figma 112:1017 is Fill, it has no minimum of its own).</summary>
    public const double HealthSpacerMinWidth = 24;

    /// <summary>Health card padding 24 x 2 + border 1 x 2 (SaGlassSurfaceStyle).</summary>
    public const double HealthCardChrome = 50;

    /// <summary>Overview page padding 40 x 2 (SaScrollPagePadding sides).</summary>
    public const double PagePaddingSides = 80;

    /// <summary>Gap between the health and priority cards (SaSpace24).</summary>
    public const double HealthRowColumnGap = 24;

    /// <summary>Health column share of the two-column row (Figma 112:1009: 624 : 392).</summary>
    public const double HealthColumnShare = 624.0 / (624 + 392);

    /// <summary>
    /// The widest count + total the two-column layout must hold on one line: "128" (Light 36, about 62), 8, and
    /// "de 256 saudáveis" (22, about 163), the measured worst case (FIX-OVERVIEW-FIT D-1).
    /// </summary>
    public const double HealthCountBudget = 62 + 8 + 163;

    /// <summary>The page viewport from which the health and priority cards sit side by side (WidthStates Wide).</summary>
    public const double TwoColumnViewport = 1000;

    /// <summary>
    /// FIX-OVERVIEW-FIT D-1 rule B (never clip the bar): true when count + 8 + total + 8 + spacer minimum + 8 + the bar
    /// do not fit the measured row, so the bar moves under the count (the NarrowWide arrangement). The boundary fits.
    /// </summary>
    public static bool HealthBarBelowCount(double rowWidth, double countWidth, double totalWidth) =>
        countWidth + HealthRowGap + totalWidth + HealthRowGap + HealthSpacerMinWidth + HealthRowGap + OverviewPresentation.HealthBarWidth > rowWidth;

    /// <summary>
    /// FIX-OVERVIEW-FIT D-1 rule A: the smallest page viewport whose two-column health card holds
    /// <paramref name="countBudget"/> (count + 8 + total), the spacer minimum and the 213 bar on one line.
    /// </summary>
    public static double MinTwoColumnViewport(double countBudget)
    {
        var row = countBudget + HealthRowGap + HealthSpacerMinWidth + HealthRowGap + OverviewPresentation.HealthBarWidth;
        var card = row + HealthCardChrome;
        return (card / HealthColumnShare) + HealthRowColumnGap + PagePaddingSides;
    }

    public static GridLength UsedStar(double percent) => new(Math.Clamp(double.IsNaN(percent) ? 0 : percent, 0, 100), GridUnitType.Star);

    public static GridLength RemainingStar(double percent) => new(100 - Math.Clamp(double.IsNaN(percent) ? 0 : percent, 0, 100), GridUnitType.Star);
}
