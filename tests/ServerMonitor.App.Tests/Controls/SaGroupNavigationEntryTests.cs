using Microsoft.UI.Xaml.Input;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.Controls;

/// <summary>
/// UI.3 SaGroupNavigation entry rules.
/// - UIA pass: a SelectionItemPattern.Select from outside the group was silently undone (the entry redirect re-checked the
///   old item through SelectionFollowsFocus).
/// - Beacon F1 (WCAG 3.2.1): the Shift+Tab WRAP from the first tab stop of the History page landed on the last segment
///   ("30 days") and selected it - focus navigation changed the data. Cortex NIT-2: an XY arrow entry would do the same.
/// Now: no focus entry ever selects; any non-pointer entry from outside lands on the checked item.
/// </summary>
public sealed class SaGroupNavigationEntryTests
{
    [Theory]
    [InlineData(FocusInputDeviceKind.Keyboard)]          // Tab, Shift+Tab, the window wrap, XY arrows from a neighbour
    [InlineData(FocusInputDeviceKind.None)]              // programmatic / UI Automation
    [InlineData(FocusInputDeviceKind.GameController)]
    public void AnyNonPointerEntryFromOutside_LandsOnTheCheckedItem(FocusInputDeviceKind device)
    {
        Assert.True(SaGroupNavigation.RedirectsEntryToChecked(device, fromInside: false));
    }

    [Theory]
    [InlineData(FocusInputDeviceKind.Mouse, false)]
    [InlineData(FocusInputDeviceKind.Touch, false)]
    [InlineData(FocusInputDeviceKind.Pen, false)]
    [InlineData(FocusInputDeviceKind.Keyboard, true)]    // moving inside the group (the arrows)
    public void PointerOrInternalFocus_LandsWhereItWasSent(FocusInputDeviceKind device, bool fromInside)
    {
        Assert.False(SaGroupNavigation.RedirectsEntryToChecked(device, fromInside));
    }

    [Fact]
    public void OnlyAnArrowMoveInsideTheGroup_Selects_NeverAFocusEntry()
    {
        Assert.True(SaGroupNavigation.SelectsOnFocusMove(SaGroupNavigationMode.SelectionFollowsFocus, arrowInsideGroup: true));
        Assert.False(SaGroupNavigation.SelectsOnFocusMove(SaGroupNavigationMode.SelectionFollowsFocus, arrowInsideGroup: false));
        Assert.False(SaGroupNavigation.SelectsOnFocusMove(SaGroupNavigationMode.FocusOnly, arrowInsideGroup: true));

        // Structural half: the selection is set only by the arrow handler - no GotFocus handler that checks an item.
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaGroupNavigation.cs");
        Assert.DoesNotContain("GotFocus", code, StringComparison.Ordinal);
        Assert.Equal(1, code.Split("IsChecked = true").Length - 1);
        var keyDown = code[code.IndexOf("private static void OnKeyDown", StringComparison.Ordinal)..];
        Assert.Contains("SelectsOnFocusMove(GetMode((Panel)sender), arrowInsideGroup: true)", keyDown, StringComparison.Ordinal);
    }
}
