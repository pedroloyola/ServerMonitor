using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Windowing;

/// <summary>
/// UI.8 D-UI8-1 / RC-1 / RC-2 (SPEC §4.10). The Compact envelope is a CLIENT-area envelope in DIP - default and maximum
/// 432×704, minimum 320×340 - converted for the TARGET monitor's scale and grown by the measured non-client frame;
/// Standard stays in pixels. Both modes are resizable and the window enforces the ACTIVE mode's limits, read from the
/// coordinator, so the Compact maximum never survives a return to Standard. Pure: fake adapter, no window.
/// </summary>
public sealed class Ui8CompactEnvelopeTests
{
    private static readonly WindowSizeConstraints Compact = WindowSizeConstraints.Compact;

    [Fact]
    public void TheEnvelopes_AreTheDecidedOnes()
    {
        Assert.Equal(new WindowSizeConstraints(320, 340, 432, 704, 432, 704, IsDipBased: true), Compact);
        Assert.Equal(new WindowSizeConstraints(560, 640, 780, 760, 20000, 20000, IsDipBased: false), WindowSizeConstraints.Standard);
    }

    [Theory]
    [InlineData(100, 320, 340, 432, 704)]
    [InlineData(125, 400, 425, 540, 880)]
    [InlineData(150, 480, 510, 648, 1056)]
    [InlineData(200, 640, 680, 864, 1408)]
    [InlineData(0, 320, 340, 432, 704)]     // an invalid scale counts as 100 %
    public void ACompactEnvelope_IsScaledForTheMonitor(int dpi, int minWidth, int minHeight, int maxWidth, int maxHeight)
    {
        var physical = Compact.ScaledTo(dpi);

        Assert.Equal((minWidth, minHeight, maxWidth, maxHeight), (physical.MinWidth, physical.MinHeight, physical.MaxWidth, physical.MaxHeight));
        Assert.Equal((maxWidth, maxHeight), (physical.DefaultWidth, physical.DefaultHeight));
        Assert.False(physical.IsDipBased);
    }

    [Theory]
    [InlineData(100, 448, 712)]
    [InlineData(150, 672, 1068)]
    [InlineData(200, 896, 1424)]
    public void TheMeasuredFrame_IsAddedAtTheTargetScale_SoTheClientIsExactly432By704Dip(int dpi, int outerWidth, int outerHeight)
    {
        // Measured on the real window at 100 %: outer 348×420, client 332×412 → frame 16×8 (UI.8 RC-2 spike).
        var frame = new WindowFrame(16, 8, 100);

        var physical = Compact.ScaledTo(dpi, frame);

        Assert.Equal((outerWidth, outerHeight), (physical.MaxWidth, physical.MaxHeight));
        Assert.Equal((outerWidth, outerHeight), (physical.DefaultWidth, physical.DefaultHeight));
    }

    [Fact]
    public void TheStandardEnvelope_StaysInPixels_AtAnyScale()
    {
        Assert.Same(WindowSizeConstraints.Standard, WindowSizeConstraints.Standard.ScaledTo(200, new WindowFrame(16, 8, 100)));
    }

    // ---- resolver (RC-1): default, legacy, off-screen, DPI 100/125/150/200, 1366×768 ----

    [Theory]
    [InlineData(100, 1920, 1040, 432, 704)]
    [InlineData(125, 2400, 1300, 540, 880)]
    [InlineData(150, 2880, 1560, 648, 1056)]
    [InlineData(200, 3840, 2080, 864, 1408)]
    [InlineData(100, 1366, 728, 432, 704)]   // 1366×768 with a 40 px taskbar: 704 fits
    [InlineData(125, 1366, 728, 540, 728)]   // the same panel at 125 %: clamped to the work area, the list scrolls
    [InlineData(150, 1920, 1032, 648, 1032)]
    public void NothingSaved_CentersTheDefaultClientSizeForTheMonitor(int dpi, int workWidth, int workHeight, int width, int height)
    {
        var display = new DisplayWorkArea(0, 0, workWidth, workHeight, dpi);

        var result = WindowPlacementResolver.Resolve(null, dpi, [display], Compact);

        Assert.Equal((width, height), (result.Width, result.Height));
        Assert.Equal((workWidth - width) / 2, result.X);
        Assert.True(result.Bottom <= display.Bottom);
    }

    [Fact]
    public void TheLegacyPersisted348By420_IsKeptAt100_AndRaisedToTheMinimumAt150()
    {
        var at100 = new DisplayWorkArea(0, 0, 1920, 1040, 100);
        var at150 = new DisplayWorkArea(0, 0, 2880, 1560, 150);
        var legacy = new WindowBounds(900, 400, 348, 420);

        Assert.Equal(legacy, WindowPlacementResolver.Resolve(legacy, 100, [at100], Compact));

        // Captured at 150 %: 348×420 px is 232×280 DIP, below the new 320×340 DIP minimum → 480×510 px.
        var raised = WindowPlacementResolver.Resolve(legacy, 150, [at150], Compact);
        Assert.Equal((480, 510), (raised.Width, raised.Height));
    }

    [Theory]
    [InlineData(100, 100, 100, 320, 340)]
    [InlineData(100, 1200, 1100, 432, 704)]
    [InlineData(200, 100, 100, 640, 680)]
    [InlineData(200, 2000, 2000, 864, 1408)]
    public void ASavedSize_IsClampedIntoTheScaledEnvelope(int dpi, int width, int height, int expectedWidth, int expectedHeight)
    {
        var display = new DisplayWorkArea(0, 0, 3840, 2080, dpi);
        var result = WindowPlacementResolver.Resolve(new WindowBounds(50, 50, width, height), dpi, [display], Compact);

        Assert.Equal((expectedWidth, expectedHeight), (result.Width, result.Height));
    }

    [Fact]
    public void AnOffScreenSave_RecentersTheScaledDefault_OnThePrimary()
    {
        var primary = new DisplayWorkArea(0, 0, 2880, 1560, 150);
        var gone = new WindowBounds(-5000, -5000, 432, 704);

        var result = WindowPlacementResolver.Resolve(gone, 100, [primary], Compact, new WindowFrame(16, 8, 100));

        Assert.Equal((672, 1068), (result.Width, result.Height));
        Assert.True(primary.ContainsPoint(result.CenterX, result.CenterY));
    }

    // ---- RC-2: limits per active mode, restored on Standard ----

    [Fact]
    public void CurrentSizeLimits_FollowTheActiveMode_AtTheWindowsDpiAndFrame_AndNeverLeaveCompactOnStandard()
    {
        var adapter = new RecordingPlacementAdapter { CurrentDpi = 150, Frame = new WindowFrame(24, 12, 150) };
        var coordinator = new WindowModeCoordinator(adapter, new FakeWindowPlacementStore(), NullLogger<WindowModeCoordinator>.Instance);
        coordinator.Initialize();
        Assert.Same(WindowSizeConstraints.Standard, coordinator.CurrentSizeLimits());

        coordinator.SwitchTo(WindowMode.Compact);
        var compact = coordinator.CurrentSizeLimits();
        Assert.Equal((504, 522, 672, 1068), (compact.MinWidth, compact.MinHeight, compact.MaxWidth, compact.MaxHeight));

        for (var i = 0; i < 9; i++)
        {
            coordinator.Toggle();
        }

        Assert.Equal(WindowMode.Standard, coordinator.CurrentMode);
        Assert.Same(WindowSizeConstraints.Standard, coordinator.CurrentSizeLimits());
        Assert.Equal((560, 640), WindowSizeConstraints.Standard.Clamp(100, 100));
        Assert.Equal((1500, 1200), coordinator.CurrentSizeLimits().Clamp(1500, 1200)); // no Compact maximum left behind
    }

    [Fact]
    public void ADraggedCompactWindow_IsHeldInsideTheEnvelope_BothWays()
    {
        var limits = Compact.ScaledTo(100, new WindowFrame(16, 8, 100));

        Assert.Equal((336, 348), limits.Clamp(200, 200));
        Assert.Equal((448, 712), limits.Clamp(1200, 1100));
        Assert.Equal((400, 500), limits.Clamp(400, 500));
    }

    [Fact]
    public void EveryTransition_ConfiguresThePresenterForItsOwnMode()
    {
        var adapter = new RecordingPlacementAdapter();
        var coordinator = new WindowModeCoordinator(adapter, new FakeWindowPlacementStore(), NullLogger<WindowModeCoordinator>.Instance);
        coordinator.Initialize();

        coordinator.SwitchTo(WindowMode.Compact);
        Assert.Equal(WindowMode.Compact, adapter.LastPresenterMode);
        coordinator.SwitchTo(WindowMode.Standard);
        Assert.Equal(WindowMode.Standard, adapter.LastPresenterMode);
    }

    /// <summary>
    /// The window side of RC-2 (WinUI, not unit-constructible): every user resize - in BOTH modes - is clamped by the
    /// coordinator's CurrentSizeLimits, with no size constant and no Standard-only guard left in MainWindow (N-2).
    /// </summary>
    [Fact]
    public void TheWindow_EnforcesTheActiveModesLimits_FromTheCoordinator_InBothModes()
    {
        var window = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        var handler = window[window.IndexOf("private void OnAppWindowChanged", StringComparison.Ordinal)..window.IndexOf("private void SchedulePersist", StringComparison.Ordinal)];

        Assert.Contains("if (args.DidSizeChange)", handler, StringComparison.Ordinal);
        Assert.Contains("EnforceSizeLimits(sender);", handler, StringComparison.Ordinal);
        Assert.Contains("_modeCoordinator.CurrentSizeLimits().Clamp(size.Width, size.Height)", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentMode == WindowMode.Standard", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("MinimumWindowWidth", window, StringComparison.Ordinal);
        Assert.DoesNotContain("560", window, StringComparison.Ordinal);
    }

    /// <summary>RC-2 spike outcome: the presenter's own min/max (unit unproven) are never set - only the manual enforcement.</summary>
    [Fact]
    public void TheAdapter_LeavesThePresenterMinMaxUnset_AndOnlyStandardIsMaximizable()
    {
        var adapter = AppSourceTree.CodeWithoutComments("Windowing/AppWindowPlacementAdapter.cs");

        Assert.DoesNotContain("PreferredMinimum", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("PreferredMaximum", adapter, StringComparison.Ordinal);
        Assert.Contains("presenter.IsResizable = true;", adapter, StringComparison.Ordinal);
        Assert.Contains("presenter.IsMaximizable = isStandard;", adapter, StringComparison.Ordinal);
    }
}
