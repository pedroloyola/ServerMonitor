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
