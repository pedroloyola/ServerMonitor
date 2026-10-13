namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 B12-07 (editor part; Beacon, Prism decision §5). The header button ("Importar de SSH" / "Voltar ao detalhe") was
/// declared last in the page so it was reached by Tab AFTER the footer actions. WinUI's default Tab order follows the tree
/// order of the focusable elements (no TabIndex is used on this page), so the order is pinned on the tree: title → header
/// button → form → footer, all in PageRoot, header button still in the header row. Real-keyboard walk: Beacon.
/// </summary>
public sealed class Ui12EditorHeaderTabOrderTests
{
    [Fact]
    public void TheHeaderButton_ComesRightAfterTheTitle_BeforeTheFormAndTheFooter()
    {
        var xaml = AppSourceTree.LoadXaml("Views/ServerEditorPage.xaml");
        var root = xaml.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "PageRoot");
        var order = root.Elements()
            .Select(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name"))
            .Where(name => name is "HeaderTitle" or "HeaderButton" or "ServerForm" or "ActionArea")
            .ToList();

        Assert.Equal(["HeaderTitle", "HeaderButton", "ServerForm", "ActionArea"], order);

        var button = root.Elements().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "HeaderButton");
        Assert.Null(button.Attribute("Grid.Row")); // the header row (0): the visual position is unchanged
        Assert.DoesNotContain(xaml.Descendants(), e => e.Attribute("TabIndex") is not null);
    }
}
