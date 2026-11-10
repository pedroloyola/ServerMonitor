namespace ServerMonitor.App.Views;

/// <summary>
/// M15.4a: where the link cards go in the Settings page's row of link cards (<c>DisclosureGrid</c>): the two built-in
/// ones ("Dados e servidores", "Sobre o ServerAlyzer") first, then the composition-supplied cards in the shell's order.
/// Wide: two per row, left to right. Narrow (below 700): one per row, as the XAML's Narrow state already does for the
/// built-in ones. Pure arithmetic, tested against <c>SettingsPage.xaml</c> without a XAML runtime.
/// </summary>
internal static class SettingsLinkCardLayout
{
    /// <summary>The built-in link cards in the XAML.</summary>
    internal const int BuiltInCards = 2;

    /// <summary>The rows the XAML declares (enough for the built-in cards stacked).</summary>
    internal const int DeclaredRows = 2;

    /// <summary>The cell of the card at <paramref name="index"/> (built-in ones first).</summary>
    internal static (int Row, int Column) Place(int index, bool narrow) => narrow ? (index, 0) : (index / 2, index % 2);

    /// <summary>The rows needed with <paramref name="contributed"/> cards, never fewer than the XAML declares.</summary>
    internal static int Rows(int contributed, bool narrow)
    {
        var cards = BuiltInCards + contributed;
        return Math.Max(DeclaredRows, narrow ? cards : (cards + 1) / 2);
    }
}
