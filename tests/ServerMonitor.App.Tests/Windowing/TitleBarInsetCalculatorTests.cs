using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Windowing;

public sealed class TitleBarInsetCalculatorTests
{
    [Fact]
    public void ZeroInset_ReservesNothing()
    {
        Assert.Equal(0, TitleBarInsetCalculator.ToReservedDips(0, 1.0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-500)]
    public void NegativeInset_ReservesNothing(int inset)
    {
        Assert.Equal(0, TitleBarInsetCalculator.ToReservedDips(inset, 1.5));
    }

    [Fact]
    public void At100Percent_ReservesTheSameDips()
    {
        // Physical == DIP at 100%.
        Assert.Equal(138, TitleBarInsetCalculator.ToReservedDips(138, 1.0));
    }

    [Theory]
    [InlineData(1.25, 111)]  // ceil(138 / 1.25) = ceil(110.4) = 111
    [InlineData(1.5, 92)]    // ceil(138 / 1.5)  = ceil(92.0)  = 92
    [InlineData(2.0, 69)]    // ceil(138 / 2.0)  = 69
    public void HigherDpi_ReservesFewerDips_PreservingPhysicalWidth(double scale, double expected)
    {
        Assert.Equal(expected, TitleBarInsetCalculator.ToReservedDips(138, scale));
    }

    [Fact]
    public void LargerInset_ReservesProportionallyMore()
    {
        // A wider caption region (e.g. more/larger buttons) reserves more space, not a fixed guess.
        var small = TitleBarInsetCalculator.ToReservedDips(92, 1.0);
        var large = TitleBarInsetCalculator.ToReservedDips(207, 1.0);
        Assert.True(large > small);
        Assert.Equal(207, large);
    }

    [Fact]
    public void NonPositiveScale_IsTreatedAsOneToOne()
    {
        Assert.Equal(138, TitleBarInsetCalculator.ToReservedDips(138, 0));
        Assert.Equal(138, TitleBarInsetCalculator.ToReservedDips(138, -2));
    }

    [Fact]
    public void AbsurdInset_IsClampedToASaneMaximum()
    {
        Assert.Equal(TitleBarInsetCalculator.MaxReserveDips, TitleBarInsetCalculator.ToReservedDips(999999, 1.0));
    }

    /// <summary>
    /// UI.10 H07: the compact title strip's 40 line starts at the measured caption height (rounded up) + 2, so the 36
    /// "Expandir" (2 below the line's top) has its -3 focus ring at caption + 1 - never inside the native caption band.
    /// </summary>
    [Theory]
    [InlineData(32, 1.0, 34)]   // Standard caption at 100 %
    [InlineData(48, 1.5, 34)]   // the same 32 DIP caption at 150 %
    [InlineData(48, 1.0, 50)]   // a taller (48) caption: the strip follows the measurement, no literal
    [InlineData(100, 3.0, 36)]  // 33.33 -> 34 (rounded up) + 2
    [InlineData(0, 1.0, 0)]     // not reported yet: the caller keeps its provisional layout
    public void TitleStripTop_IsTheMeasuredCaptionHeightPlusTwo(int captionPx, double scale, double expected)
    {
        Assert.Equal(expected, TitleBarInsetCalculator.ToTitleStripTopDips(captionPx, scale));
        if (expected > 0)
        {
            var caption = TitleBarInsetCalculator.ToReservedDips(captionPx, scale);
            var focusRingTop = expected + TitleBarInsetCalculator.ButtonInsetOnStripLine - 3;
            Assert.True(focusRingTop >= caption + 1, $"focus ring top {focusRingTop} must clear the caption {caption}");
        }
    }

    [Fact]
    public void FractionalResult_IsRoundedUp_SoNoOnePixelOverlapRemains()
    {
        // 100 / 3 = 33.33 -> must round up to 34 to fully clear the caption region.
        Assert.Equal(34, TitleBarInsetCalculator.ToReservedDips(100, 3.0));
    }
}
