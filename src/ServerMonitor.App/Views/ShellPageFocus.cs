using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
namespace ServerMonitor.App.Views;
/// <summary>One shell policy: keyboard sidebar activation keeps focus; content entry targets H1 unless a return slot wins.</summary>
public static class ShellPageFocus
{
    public static readonly DependencyProperty KeepSidebarProperty = DependencyProperty.RegisterAttached("KeepSidebar",typeof(bool),typeof(ShellPageFocus),new PropertyMetadata(false));
    public static bool GetKeepSidebar(Page page) => (bool)page.GetValue(KeepSidebarProperty);
    public static void SetKeepSidebar(Page page,bool value) => page.SetValue(KeepSidebarProperty,value);
    public static void FocusHeading(Page page, bool force = false)
    {
        if (!force && GetKeepSidebar(page)) return;
        if (FindHeading(page) is { } heading) _ = FocusManager.TryFocusAsync(heading,FocusState.Programmatic);
    }
    public static FrameworkElement? FindHeading(DependencyObject root)
    {
        if (root is FrameworkElement element && AutomationProperties.GetHeadingLevel(element) == AutomationHeadingLevel.Level1) return element;
        for (var i=0; i<VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindHeading(VisualTreeHelper.GetChild(root,i)) is { } found) return found;
        return null;
    }
}
