namespace ServerMonitor.App.Views;

/// <summary>
/// M15.4a r4 (Figma, owner decision): where the link cards go in the Settings page's row of link cards
/// (<c>DisclosureGrid</c>). The order is "Dados e servidores", then the composition-supplied cards in the shell's order,
/// then "Sobre o ServerAlyzer" (so one contributed card sits in the middle). Wide: three per row with a contributed card,
/// two without (the XAML's row, unchanged). Narrow (below 700): one per row, same order. Pure arithmetic, tested against
/// <c>SettingsPage.xaml</c> without a XAML runtime.
/// </summary>
internal static class SettingsLinkCardLayout
{
    /// <summary>The built-in link cards in the XAML.</summary>
    internal const int BuiltInCards = 2;

    /// <summary>The rows the XAML declares (enough for the built-in cards stacked).</summary>
    internal const int DeclaredRows = 2;

    /// <summary>The columns the XAML declares (the built-in row).</summary>
    internal const int DeclaredColumns = 2;

    /// <summary>Wide, with at least one contributed card: three per row.</summary>
    internal const int WideColumnsWithCards = 3;

    /// <summary>The order of "Dados e servidores".</summary>
    internal const int DataSequence = 0;

    /// <summary>The order of the contributed card at <paramref name="index"/>.</summary>
    internal static int ContributedSequence(int index) => DataSequence + 1 + index;

    /// <summary>The order of "Sobre o ServerAlyzer": after every contributed card.</summary>
    internal static int AboutSequence(int contributed) => DataSequence + 1 + contributed;

    /// <summary>The columns in use.</summary>
    internal static int Columns(int contributed, bool narrow) =>
        narrow ? 1 : contributed == 0 ? DeclaredColumns : WideColumnsWithCards;

    /// <summary>The cell of the card at <paramref name="sequence"/>, left to right then down.</summary>
    internal static (int Row, int Column) Place(int sequence, int contributed, bool narrow)
    {
        var columns = Columns(contributed, narrow);
        return (sequence / columns, sequence % columns);
    }

    /// <summary>The rows holding a card.</summary>
    internal static int UsedRows(int contributed, bool narrow)
    {
        var columns = Columns(contributed, narrow);
        return (BuiltInCards + contributed + columns - 1) / columns;
    }

    /// <summary>The rows the grid needs, never fewer than the XAML declares (an unused Auto row takes no space).</summary>
    internal static int Rows(int contributed, bool narrow) => Math.Max(DeclaredRows, UsedRows(contributed, narrow));
}
