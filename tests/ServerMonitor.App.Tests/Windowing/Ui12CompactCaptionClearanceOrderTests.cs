using System.Text.RegularExpressions;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Windowing;

/// <summary>
/// UI.12 C-W4 (Cortex UI.12A, UI10-H07-DPI-ORDER). The Compact caption clearance (AppWindow.TitleBar.Height in physical px ÷
/// the XamlRoot scale) was recomputed only on XamlRoot.Changed, entry into Compact and Loaded. On a move to a monitor with
/// another DPI, if TitleBar.Height updates AFTER XamlRoot.Changed (order neither documented nor measured - single 100 %
/// monitor here, runtime NOT_RUN), the strip kept the old DPI's height. AppWindow.Changed (move/resize) in Compact now
/// recomputes too. The simulation below drives both event orders through the real pure pieces (the decision and
/// TitleBarInsetCalculator); the handler wiring is pinned by source.
/// </summary>
public sealed partial class Ui12CompactCaptionClearanceOrderTests
{
    [Theory]
    [InlineData(true)]  // the caption height updates BEFORE XamlRoot.Changed
    [InlineData(false)] // ...or AFTER it (the unmeasured, problematic order)
    public void TheClearance_FollowsTheLastReading_WhicheverEventComesLast(bool heightFirst)
    {
        var display = new Display(captionHeightPx: 32, scale: 1.0);
        display.Recompute();

        // Move to a 150 % monitor: the scale changes (XamlRoot.Changed) and the caption height changes.
        if (heightFirst)
        {
            display.CaptionHeightPx = 48;
            display.Scale = 1.5;
            display.Recompute();                      // XamlRoot.Changed
        }
        else
        {
            display.Scale = 1.5;
            display.Recompute();                      // XamlRoot.Changed (height still the old one)
            display.CaptionHeightPx = 48;
        }

        display.OnAppWindowChanged(WindowMode.Compact, didSizeChange: true, didPositionChange: true);

        Assert.Equal(TitleBarInsetCalculator.ToTitleStripTopDips(48, 1.5), display.Top);
    }

    [Theory]
    [InlineData(WindowMode.Compact, true, false, true)]
    [InlineData(WindowMode.Compact, false, true, true)]
    [InlineData(WindowMode.Compact, false, false, false)]
    [InlineData(WindowMode.Standard, true, true, false)] // Standard has no compact strip
    public void TheDecision(WindowMode mode, bool size, bool position, bool refreshes) =>
        Assert.Equal(refreshes, MainWindow.RefreshesCompactClearanceOnAppWindowChange(mode, size, position));

    [Fact]
    public void OnAppWindowChanged_RecomputesTheClearance_ThroughTheDecision()
    {
        var code = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        Assert.Matches(Wiring(), code);
    }

    [GeneratedRegex(@"private void OnAppWindowChanged\(AppWindow sender, AppWindowChangedEventArgs args\)[\s\S]*?if \(RefreshesCompactClearanceOnAppWindowChange\(_modeCoordinator\.CurrentMode, args\.DidSizeChange, args\.DidPositionChange\)\)\s*\{\s*UpdateCompactCaptionClearance\(\);\s*\}")]
    private static partial Regex Wiring();

    // The window's clearance state: what UpdateCompactCaptionClearance reads (height, scale) and writes (top).
    private sealed class Display(int captionHeightPx, double scale)
    {
        public int CaptionHeightPx { get; set; } = captionHeightPx;

        public double Scale { get; set; } = scale;

        public double Top { get; private set; }

        public void Recompute() => Top = TitleBarInsetCalculator.ToTitleStripTopDips(CaptionHeightPx, Scale);

        public void OnAppWindowChanged(WindowMode mode, bool didSizeChange, bool didPositionChange)
        {
            if (MainWindow.RefreshesCompactClearanceOnAppWindowChange(mode, didSizeChange, didPositionChange))
            {
                Recompute();
            }
        }
    }
}
