namespace ServerMonitor.App.Controls;

/// <summary>
/// M15.4a: where contributed navigation entries go in the sidebar's grid, and their place in the Narrator's set. Pure
/// arithmetic over the layout of <c>SaSidebar.xaml</c> (brand, three destinations, the flexible spacer, Settings), kept
/// apart so it is tested against that XAML without a XAML runtime.
/// </summary>
internal static class SidebarContributionLayout
{
    /// <summary>The flexible spacer's row: contributed entries take new rows here, so Settings stays at the bottom.</summary>
    internal const int FirstContributedRow = 4;

    /// <summary>Settings' row in the XAML. The footer link takes a new row here, so it sits just above Settings.</summary>
    internal const int SettingsRow = 5;

    /// <summary>M15.4a r4 (Figma, owner decision): the expanded footer link is a 176x36 pill with radius 12.</summary>
    internal const double FooterLinkHeight = 36;

    internal const double FooterLinkCornerRadius = 12;

    /// <summary>In the rail the footer link keeps the items' 46 circle (the Figma has no rail design).</summary>
    internal const double RailFooterLinkHeight = 46;

    internal const double RailFooterLinkCornerRadius = 23;

    /// <summary>The footer link's text: the label, then the hint after a middle dot when there is one.</summary>
    internal static string FooterLinkText(string label, string? hint) =>
        string.IsNullOrWhiteSpace(hint) ? label : label + " · " + hint;

    /// <summary>
    /// M15.4a r5 (Prism INFO-1): the footer link's tooltip, the whole line: always in the rail (the text is hidden), and
    /// expanded only when the line is trimmed; none when it fits.
    /// </summary>
    internal static string? FooterLinkToolTip(bool rail, bool trimmed, string text) => rail || trimmed ? text : null;

    /// <summary>The footer link's row, before the contributed entries are inserted (they move it down with Settings).</summary>
    internal const int FooterRow = SettingsRow;

    /// <summary>The row of an existing child once the footer row is inserted: Settings moves one down.</summary>
    internal static int RowAfterFooter(int row) => row >= FooterRow ? row + 1 : row;

    /// <summary>The row span of an existing child once the footer row is inserted (the indicator keeps spanning all).</summary>
    internal static int RowSpanAfterFooter(int row, int rowSpan) => row < FooterRow && row + rowSpan > FooterRow ? rowSpan + 1 : rowSpan;

    /// <summary>The built-in destinations (radio buttons), Settings included.</summary>
    internal const int BuiltInDestinations = 4;

    /// <summary>A contributed row is as tall as a built-in destination's row.</summary>
    internal const double ContributedRowHeight = 46;

    /// <summary>The row of an existing child once <paramref name="count"/> rows are inserted.</summary>
    internal static int RowAfterInsert(int row, int count) => row >= FirstContributedRow ? row + count : row;

    /// <summary>The row span of an existing child once <paramref name="count"/> rows are inserted (the indicator spans all).</summary>
    internal static int RowSpanAfterInsert(int row, int rowSpan, int count) =>
        row < FirstContributedRow && rowSpan > 1 ? rowSpan + count : rowSpan;

    /// <summary>The row of the contributed entry at <paramref name="index"/>.</summary>
    internal static int RowOfContributed(int index) => FirstContributedRow + index;

    /// <summary>The position in the set of the contributed entry at <paramref name="index"/>: after the three destinations.</summary>
    internal static int PositionOfContributed(int index) => BuiltInDestinations + index;

    /// <summary>The size of the set with <paramref name="count"/> contributed entries; Settings is its last position.</summary>
    internal static int SizeOfSet(int count) => BuiltInDestinations + count;
}
