namespace ServerMonitor.App.Services;
/// <summary>DERIVED Windows rail: W1 starts at 1040; verified against the host during UI.6 QA.</summary>
public static class ShellLayout
{
    public const double ExpandedWidth = 208;
    public const double RailWidth = 80;
    public const double RailBreakpoint = 1040;
    public static double SidebarWidth(double windowWidth) => windowWidth >= RailBreakpoint ? ExpandedWidth : RailWidth;
}
