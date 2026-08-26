using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Windowing;

public sealed class WindowPlacementResolverTests
{
    private static readonly WindowSizeConstraints Standard = WindowSizeConstraints.Standard;
    private static readonly WindowSizeConstraints Compact = WindowSizeConstraints.Compact;

    // A single 1920×1040 (work area) primary display at the origin, 100% scale.
    private static readonly DisplayWorkArea Primary = new(0, 0, 1920, 1040, 100);

    [Fact]
    public void NullSaved_CentersDefaultOnPrimary()
    {
        var result = WindowPlacementResolver.Resolve(null, 100, [Primary], Standard);

        Assert.Equal(Standard.DefaultWidth, result.Width);
        Assert.Equal(Standard.DefaultHeight, result.Height);
        Assert.Equal((1920 - Standard.DefaultWidth) / 2, result.X);
        Assert.Equal((1040 - Standard.DefaultHeight) / 2, result.Y);
    }

    [Theory]
    [InlineData(0, 400)]      // zero width
    [InlineData(-10, 400)]    // negative width
    [InlineData(400, 0)]      // zero height
    [InlineData(99999, 400)]  // absurd width
    [InlineData(400, 99999)]  // absurd height
    public void MalformedSaved_FallsBackToDefault(int width, int height)
    {
        var saved = new WindowBounds(100, 100, width, height);

        var result = WindowPlacementResolver.Resolve(saved, 100, [Primary], Standard);

        Assert.Equal(Standard.DefaultWidth, result.Width);
        Assert.Equal(Standard.DefaultHeight, result.Height);
    }

    [Fact]
    public void AbsurdCoordinates_FallBackToDefault()
    {
        var saved = new WindowBounds(500000, 500000, 700, 700);

        var result = WindowPlacementResolver.Resolve(saved, 100, [Primary], Standard);

        Assert.True(Primary.ContainsPoint(result.CenterX, result.CenterY));
    }

    [Fact]
    public void ValidSavedWithinDisplay_IsPreserved()
    {
        var saved = new WindowBounds(200, 150, 800, 700);

        var result = WindowPlacementResolver.Resolve(saved, 100, [Primary], Standard);

        Assert.Equal(saved, result);
    }

    [Fact]
    public void SavedOnRightMonitor_StaysOnThatMonitor()
    {
        var right = new DisplayWorkArea(1920, 0, 1920, 1040, 100);
        var saved = new WindowBounds(2200, 200, 800, 700);

        var result = WindowPlacementResolver.Resolve(saved, 100, [Primary, right], Standard);

        Assert.Equal(saved, result);
        Assert.True(right.ContainsPoint(result.CenterX, result.CenterY));
    }

    [Fact]
    public void SavedOnLeftMonitorWithNegativeCoordinates_IsPreserved()
    {
        var left = new DisplayWorkArea(-1920, 0, 1920, 1040, 100);
        var saved = new WindowBounds(-1600, 200, 800, 700);

        var result = WindowPlacementResolver.Resolve(saved, 100, [Primary, left], Standard);

        Assert.Equal(saved, result);
    }

    [Fact]
    public void SavedOnMonitorAbove_WithNegativeY_IsPreserved()
    {
        var above = new DisplayWorkArea(0, -1080, 1920, 1040, 100);
        var saved = new WindowBounds(300, -900, 800, 700);

        var result = WindowPlacementResolver.Resolve(saved, 100, [Primary, above], Standard);

        Assert.Equal(saved, result);
    }

    [Fact]
    public void SavedMonitorMissing_RecoversToPrimary()
    {
        // Bounds were saved on a right monitor that is no longer connected.
        var saved = new WindowBounds(2400, 200, 800, 700);

        var result = WindowPlacementResolver.Resolve(saved, 100, [Primary], Standard);

        Assert.True(Primary.ContainsPoint(result.CenterX, result.CenterY));
        Assert.Equal(Standard.DefaultWidth, result.Width);
    }

    [Fact]
    public void SavedPartlyOffScreen_IsClampedFullyOnScreen()
    {
        // Overlaps the primary but hangs off the right and bottom edges.
        var saved = new WindowBounds(1700, 900, 800, 700);

        var result = WindowPlacementResolver.Resolve(saved, 100, [Primary], Standard);

        Assert.True(result.X >= Primary.X);
        Assert.True(result.Y >= Primary.Y);
        Assert.True(result.Right <= Primary.Right);
        Assert.True(result.Bottom <= Primary.Bottom);
        Assert.Equal(800, result.Width);
        Assert.Equal(700, result.Height);
    }

    [Fact]
    public void HigherTargetDpi_ScalesLogicalSizeUp()
    {
        var hiDpi = new DisplayWorkArea(0, 0, 3840, 2080, 200);
        var saved = new WindowBounds(100, 100, 400, 380); // captured at 100%

        var result = WindowPlacementResolver.Resolve(saved, 100, [hiDpi], Compact);

        // 400×380 logical at 100% → 800×760 physical at 200%, then clamped to the compact envelope.
        Assert.Equal(Math.Min(800, Compact.MaxWidth), result.Width);
        Assert.Equal(Math.Min(760, Compact.MaxHeight), result.Height);
    }

    [Fact]
    public void LowerTargetDpi_ScalesLogicalSizeDown()
    {
        var loDpi = new DisplayWorkArea(0, 0, 1920, 1040, 100);
        var saved = new WindowBounds(100, 100, 700, 700); // captured at 200%

        var result = WindowPlacementResolver.Resolve(saved, 200, [loDpi], Standard);

        // 700×700 physical at 200% → 350×350 logical → 350×350 physical at 100%, floored to the minimum.
        Assert.Equal(Standard.MinWidth, result.Width);
        Assert.Equal(Standard.MinHeight, result.Height);
    }

    [Fact]
    public void WindowLargerThanWorkArea_IsClampedToWorkAreaAtOrigin()
    {
        var small = new DisplayWorkArea(0, 0, 500, 500, 100);
        var saved = new WindowBounds(50, 50, 480, 480);

        var result = WindowPlacementResolver.Resolve(saved, 100, [small], Standard);

        // Standard minimum (560×640) exceeds this tiny display; clamp to the display and pin to origin.
        Assert.Equal(small.X, result.X);
        Assert.Equal(small.Y, result.Y);
        Assert.True(result.Width <= small.Width);
        Assert.True(result.Height <= small.Height);
    }

    [Fact]
    public void NegativeCoordinatePrimary_CentersDefaultCorrectly()
    {
        var primaryLeft = new DisplayWorkArea(-1920, -200, 1920, 1040, 100);

        var result = WindowPlacementResolver.Resolve(null, 100, [primaryLeft], Standard);

        Assert.True(primaryLeft.ContainsPoint(result.CenterX, result.CenterY));
    }

    [Fact]
    public void NoDisplays_StillReturnsUsableRectangle()
    {
        var result = WindowPlacementResolver.Resolve(null, 100, [], Compact);

        Assert.True(result.Width > 0);
        Assert.True(result.Height > 0);
    }
}
