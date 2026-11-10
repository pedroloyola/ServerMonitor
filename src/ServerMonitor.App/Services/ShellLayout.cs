namespace ServerMonitor.App.Services;
/// <summary>DERIVED Windows rail: W1 starts at 1040; verified against the host during UI.6 QA.</summary>
public static class ShellLayout
{
    /// <summary>M15.4a owner decision: 224 (padding 24 + item 176 + 24), was 208.</summary>
    public const double ExpandedWidth = 224;

    /// <summary>The width of every expanded sidebar item (built-in, contributed, the footer link): 176, was 160.</summary>
    public const double ItemWidth = 176;

    /// <summary>The width of every item in the rail: a circle.</summary>
    public const double RailItemWidth = 46;

    /// <summary>The label's room in an expanded item: 176 - padding 12 + 12 - icon 20 - gap 10.</summary>
    public const double ItemLabelMaxWidth = 122;
    public const double RailWidth = 80;
    public const double RailBreakpoint = 1040;
    public static double SidebarWidth(double windowWidth) => windowWidth >= RailBreakpoint ? ExpandedWidth : RailWidth;
}
