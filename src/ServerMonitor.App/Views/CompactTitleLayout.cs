namespace ServerMonitor.App.Views;

/// <summary>
/// UI.8 Prism R-3: what the Compact title strip shows at a given width, decided by MEASUREMENT (no pixel breakpoint).
/// Priority when space runs out: the native captions (never overlapped - their reserve is outside <c>available</c>) >
/// the "Expandir" button (text + icon, then icon only) > the brand mark > the wordmark (trims, then collapses). Pure.
/// </summary>
internal static class CompactTitleLayout
{
    /// <summary>Gap between mark, wordmark and button (SaSpace12, Figma g12).</summary>
    public const double Gap = 12;

    /// <summary>The icon-only button (36x36, Figma h36).</summary>
    public const double IconButtonWidth = 36;

    /// <summary>Below this the wordmark is collapsed rather than shown as an ellipsis stub.</summary>
    public const double MinimumWordmark = 40;

    public readonly record struct Decision(bool ShowExpandText, bool ShowMark, bool ShowWordmark);

    /// <param name="available">Strip width between the left padding and the caption reserve (W − 20 − R).</param>
    /// <param name="mark">The brand mark's width.</param>
    /// <param name="wordmark">The wordmark's DESIRED (untrimmed) width.</param>
    /// <param name="buttonWithText">The "Expandir" button's desired width with its text.</param>
    public static Decision Decide(double available, double mark, double wordmark, double buttonWithText)
    {
        var showText = available >= mark + Gap + wordmark + Gap + buttonWithText;
        var button = showText ? buttonWithText : IconButtonWidth;
        var showMark = available >= mark + Gap + button;
        var forWordmark = available - button - Gap - (showMark ? mark + Gap : 0);
        return new Decision(showText, showMark, forWordmark >= MinimumWordmark);
    }
}
