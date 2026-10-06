using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.7A structural contracts of the editor page that the runtime-free tests cannot reach: the real page delegates every
/// decision to the tested controller, it is per visit (never cached or a singleton, CP-10), the tab order (B-20), the
/// single submit path's guard (CP-11), and the copy (pt-PT "tu", pt-BR, en-US for every new key).
/// </summary>
public sealed class Ui7EditorContractTests
{
    private static readonly string[] Cultures = ["pt-PT", "pt-BR", "en-US"];

    [Fact]
    public void ThePage_DelegatesItsGuardExitsAndSubmitToTheController()
    {
        var code = AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs");

        // UI.7B: an open dialog is closed (a trust prompt dismissed) before the guard asks; the decision stays the controller's.
        Assert.Contains("await CloseDialogAsync();", code, StringComparison.Ordinal);
        Assert.Contains("return await _controller.ConfirmLeaveAsync();", code, StringComparison.Ordinal);
        Assert.Contains("_controller.Escape();", code, StringComparison.Ordinal);
        Assert.Contains("_controller.SubmitAsync(ServerForm.CaptureSecret)", code, StringComparison.Ordinal);
        Assert.Contains("_controller.Dispose();", code, StringComparison.Ordinal);
        Assert.Contains("ServerForm.ClearSecrets();", code, StringComparison.Ordinal);
        Assert.Equal(2, Count(code, "_controller.Cancel();")); // Cancelar and "Voltar ao detalhe"
        // One submit path (button and Enter), and Enter is refused while a test or a trust write runs.
        Assert.Equal(1, Count(code, "_controller.SubmitAsync("));
        Assert.Contains("IsConnectionWorkInProgress: false", code, StringComparison.Ordinal);
        // UI.7A fix c1 (M-1): while a Save is persisted, Cancelar / the header button / the actions are disabled.
        Assert.Contains("_controller.SavingChanged += (_, _) => UpdateActionState();", code, StringComparison.Ordinal);
        Assert.Contains("CancelButton.IsEnabled = !saving;", code, StringComparison.Ordinal);
        Assert.Contains("HeaderButton.IsEnabled = !saving &&", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePage_IsPerVisit_NeverCached_NeverASingleton()
    {
        var root = AppSourceTree.LoadXaml("Views/ServerEditorPage.xaml").Root!;
        Assert.Equal("Disabled", (string?)root.Attribute("NavigationCacheMode"));

        var app = AppSourceTree.CodeWithoutComments("App.xaml.cs");
        Assert.Contains("services.AddTransient<ServerEditorPage>();", app, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSingleton<ServerEditorPage>", app, StringComparison.Ordinal);
        Assert.Contains("IServerEditorView, INavigationExitGuard, IDisposable", AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs"), StringComparison.Ordinal);
    }

    /// <summary>B-20: H1 on entry (programmatic), then Nome first; the header button is drawn in the header but tabbed last.</summary>
    [Fact]
    public void TabOrder_NomeIsTheFirstStop_AndTheHeaderButtonTheLast()
    {
        var page = AppSourceTree.LoadXaml("Views/ServerEditorPage.xaml");
        var pageRoot = page.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "PageRoot");
        var children = pageRoot.Elements().Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal)).ToList();
        Assert.Equal("HeaderButton", (string?)children[^1].Attribute(X + "Name"));
        Assert.Null(children[^1].Attribute("Grid.Row")); // drawn in row 0, the header

        var form = AppSourceTree.LoadXaml("Controls/ServerFormControl.xaml");
        var cards = form.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "CardsGrid");
        var firstInput = cards.Descendants().First(e => e.Name.LocalName is "TextBox" or "PasswordBox" or "ComboBox" or "Button" or "RadioButton");
        Assert.Equal("NameField", (string?)firstInput.Attribute(X + "Name"));
        // Nothing focusable precedes the cards in the form except the temporary import list (Add only, opened on demand).
        var formRoot = form.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "FormRoot");
        Assert.DoesNotContain(
            formRoot.Elements().TakeWhile(e => e != cards).Descendants(),
            e => e.Name.LocalName is "TextBox" or "PasswordBox" or "RadioButton");
    }

    /// <summary>B-19: Porta lives in the options card (G-27), and the stacked state moves the cards and wraps the options.</summary>
    [Fact]
    public void TheForm_StacksBelowItsBreakpoint_AndPortaIsInTheOptionsCard()
    {
        var form = AppSourceTree.LoadXaml("Controls/ServerFormControl.xaml");
        var options = form.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "OptionsCard");
        Assert.Contains(options.Descendants(), e => (string?)e.Attribute(X + "Uid") == "ServerEditorPortField");

        var stacked = form.Descendants().Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(X + "Name") == "Stacked");
        var targets = stacked.Descendants().Where(e => e.Name.LocalName == "Setter").Select(e => (string)e.Attribute("Target")!).ToHashSet();
        Assert.Contains("AuthCard.(Grid.Row)", targets);
        Assert.Contains("JumpAuthCard.(Grid.Row)", targets);
        Assert.Contains("RefreshIntervalField.(Grid.Row)", targets);
        var trigger = stacked.Descendants().Single(e => e.Name.LocalName == "SaContentWidthTrigger");
        Assert.Equal("760", (string?)trigger.Attribute("MaxWidth"));
    }

    [Fact]
    public void EveryEditorKey_ExistsInEveryCulture_AndPtPtSaysTu()
    {
        var keys = Resources("pt-PT").Keys.Where(key => key.StartsWith("ServerEditor", StringComparison.Ordinal)).ToList();
        Assert.True(keys.Count >= 70, $"only {keys.Count} editor keys");
        foreach (var culture in Cultures)
        {
            var resources = Resources(culture);
            Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(resources.GetValueOrDefault(key)), $"{culture}: {key}"));
        }

        var ptPt = Resources("pt-PT");
        Assert.All(keys, key => Assert.DoesNotContain("você", ptPt[key], StringComparison.OrdinalIgnoreCase));

        // Every x:Uid the page and the form use resolves in every culture.
        var uids = AppSourceTree.LoadXaml("Views/ServerEditorPage.xaml").Descendants()
            .Concat(AppSourceTree.LoadXaml("Controls/ServerFormControl.xaml").Descendants())
            .Select(e => (string?)e.Attribute(X + "Uid")).OfType<string>().Distinct().ToList();
        foreach (var culture in Cultures)
        {
            var names = Resources(culture).Keys.ToList();
            Assert.All(uids, uid => Assert.Contains(names, name => name.StartsWith(uid + ".", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void TheLegacyEditorEntryPoints_AreGoneFromTheDialogService()
    {
        var dialogs = AppSourceTree.CodeWithoutComments("Services/IServerDialogService.cs");
        Assert.DoesNotContain("ShowEditor", dialogs, StringComparison.Ordinal);
        Assert.Contains("ConfirmRemoveAsync", dialogs, StringComparison.Ordinal);

        var dashboard = AppSourceTree.CodeWithoutComments("ViewModels/DashboardViewModel.cs");
        foreach (var open in new[] { "_editorSession.OpenAddAsync(", "_editorSession.OpenSshImportAsync(", "_editorSession.OpenDiscoveryAsync(", "_editorSession.OpenEditAsync(" })
        {
            Assert.Equal(1, Count(dashboard, open));
        }

        Assert.DoesNotContain("ShowEditor", dashboard, StringComparison.Ordinal);
    }

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static Dictionary<string, string> Resources(string culture) =>
        XDocument.Load(AppSourceTree.Full($"Resources/{culture}/Resources.resw")).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty, StringComparer.Ordinal);

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
