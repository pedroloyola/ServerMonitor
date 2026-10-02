using Microsoft.UI.Xaml.Input;
using ServerMonitor.App.Controls.Primitives;

namespace ServerMonitor.App.Tests.Controls;

/// <summary>
/// UI.3 (found by the UIA screenshot pass): selecting a segment through UI Automation (SelectionItemPattern.Select, as a
/// screen reader does) from outside the group was silently undone - the Tab-entry redirect moved focus to the
/// previously checked item and SelectionFollowsFocus re-checked it. Only a Tab / Shift+Tab entry is redirected now.
/// </summary>
public sealed class SaGroupNavigationEntryTests
{
    [Theory]
    [InlineData(FocusNavigationDirection.Next)]
    [InlineData(FocusNavigationDirection.Previous)]
    public void TabEntryFromOutside_IsRedirectedToTheCheckedItem(FocusNavigationDirection direction)
    {
        Assert.True(SaGroupNavigation.RedirectsEntryToChecked(FocusInputDeviceKind.Keyboard, direction, fromInside: false));
    }

    [Theory]
    [InlineData(FocusInputDeviceKind.Keyboard, FocusNavigationDirection.None, false)]   // UIA / programmatic focus
    [InlineData(FocusInputDeviceKind.Mouse, FocusNavigationDirection.None, false)]
    [InlineData(FocusInputDeviceKind.Keyboard, FocusNavigationDirection.Next, true)]    // moving inside the group
    [InlineData(FocusInputDeviceKind.Keyboard, FocusNavigationDirection.Left, false)]
    [InlineData(FocusInputDeviceKind.None, FocusNavigationDirection.Next, false)]
    public void ProgrammaticOrInternalFocus_LandsWhereItWasSent(FocusInputDeviceKind device, FocusNavigationDirection direction, bool fromInside)
    {
        Assert.False(SaGroupNavigation.RedirectsEntryToChecked(device, direction, fromInside));
    }
}
