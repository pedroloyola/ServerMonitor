namespace ServerMonitor.App.Views;

/// <summary>
/// M15.4a r4 (Figma, owner decision): where the link cards go in the Settings page's row of link cards
/// (<c>DisclosureGrid</c>). The order is "Dados e servidores", then the composition-supplied cards in the shell's order,
/// then "Sobre o ServerAlyzer" (so one contributed card sits in the middle). Wide: three per row with a contributed card,
/// two without (the XAML's row, unchanged). One per row, same order, below 700 and (FIX-OVERVIEW-FIT D-3) whenever the
/// widest card text would not fit its column on one line. Pure arithmetic, tested against <c>SettingsPage.xaml</c>
/// without a XAML runtime.
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

    /// <summary>D-3: the page's Narrow trigger (below it the cards are always one per row).</summary>
    internal const double NarrowViewport = 700;

    /// <summary>D-3: the page's side padding, 40 + 40 (SaScrollPagePadding).</summary>
    internal const double PageSides = 80;

    /// <summary>D-3: the gap between cards in a row (DisclosureGrid.ColumnSpacing, SaSpace24).</summary>
    internal const double CardGap = 24;

    /// <summary>D-3: a card around its text: padding 20 + 20, icon 18, gaps 12 + 12, arrow 17, border 1 + 1.</summary>
    internal const double CardChrome = 101;

    /// <summary>The order of the contributed card at <paramref name="index"/>.</summary>
    internal static int ContributedSequence(int index) => DataSequence + 1 + index;

    /// <summary>The order of "Sobre o ServerAlyzer": after every contributed card.</summary>
    internal static int AboutSequence(int contributed) => DataSequence + 1 + contributed;

    /// <summary>
    /// FIX-OVERVIEW-FIT D-3: the columns in use. The wide count (three with a contributed card, two without) only when
    /// the widest card text fits its column on one line; otherwise one per row (the Narrow arrangement). Driven by the
    /// viewport and the texts, never by the cards' own width, so it cannot oscillate. The boundary fits.
    /// </summary>
    internal static int Columns(int contributed, double viewport, double widestText)
    {
        if (viewport < NarrowViewport)
        {
            return 1;
        }

        var wide = contributed == 0 ? DeclaredColumns : WideColumnsWithCards;
        return widestText <= TextRoom(wide, viewport) ? wide : 1;
    }

    /// <summary>The width a card's text gets with <paramref name="columns"/> cards per row.</summary>
    internal static double TextRoom(int columns, double viewport) =>
        ((viewport - PageSides - (CardGap * (columns - 1))) / columns) - CardChrome;

    /// <summary>The cell of the card at <paramref name="sequence"/>, left to right then down, with <paramref name="columns"/> per row.</summary>
    internal static (int Row, int Column) Place(int sequence, int columns) => (sequence / columns, sequence % columns);

    /// <summary>The rows holding a card.</summary>
    internal static int UsedRows(int contributed, int columns) => (BuiltInCards + contributed + columns - 1) / columns;

    /// <summary>The rows the grid needs, never fewer than the XAML declares (an unused Auto row takes no space).</summary>
    internal static int Rows(int contributed, int columns) => Math.Max(DeclaredRows, UsedRows(contributed, columns));

    /// <summary>
    /// FIX-OVERVIEW-FIT D-4: a contributed card joins the grid's children just before "Sobre o ServerAlyzer", so the
    /// tree order (Tab, Shift+Tab, Narrator, UI Automation) is the visual order: "Dados e servidores", the contributed
    /// cards in the shell's order, then "Sobre". Without "Sobre" among the children it is added last.
    /// </summary>
    internal static void AddBefore<T>(IList<T> children, T about, T card)
    {
        var index = children.IndexOf(about);
        if (index < 0)
        {
            children.Add(card);
            return;
        }

        children.Insert(index, card);
    }
}
