using System.Xml.Linq;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.7C static contracts on the page code-behind that the XAML-free world cannot run (Cortex 7A m-4), the test dialog's
/// place in the ONE in-page layer, the per-field error language and the new component styles (guarded like 7B's picker).
/// </summary>
public sealed class Ui7CContractTests
{
    private static string Page => AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs");

    /// <summary>Cortex m-4: the real page ends its visit through the controller, and its exit guard IS the controller's.</summary>
    [Fact]
    public void M4_ThePageDispose_DisposesTheController_AndItsExitGuardDelegatesToIt()
    {
        var page = Page;
        var dispose = page[page.IndexOf("public void Dispose()", StringComparison.Ordinal)..];
        dispose = dispose[..dispose.IndexOf("private void OnLoaded", StringComparison.Ordinal)];
        Assert.Contains("_controller.Dispose();", dispose, StringComparison.Ordinal);
        Assert.Contains("ServerForm.ClearSecrets();", dispose, StringComparison.Ordinal);
        Assert.True(dispose.IndexOf("_controller.Dispose();", StringComparison.Ordinal) < dispose.IndexOf("HideLayer();", StringComparison.Ordinal),
            "the controller goes first, so hiding the layer dismisses / applies nothing");
        // Final c2 (B-2): the page's guard IS the controller's task - returned as is, the focus restore rides beside it.
        Assert.Contains("var answer = _controller.ConfirmLeaveAsync();", page, StringComparison.Ordinal);
        Assert.Contains("return answer;", page, StringComparison.Ordinal);
    }

    /// <summary>Cortex 7A m-3: back from the editor opened by a suggestion's "Adicionar", focus returns to that row's button.</summary>
    [Fact]
    public void M3_ASuggestionsAdicionar_IsCapturedAsItsRow_AndTheOverviewFocusesItFirst()
    {
        var origin = AppSourceTree.CodeWithoutComments("Services/FocusOrigin.cs");
        Assert.Contains("DataContext: DiscoveredServerViewModel suggestion", origin, StringComparison.Ordinal);
        Assert.Contains("EditorOpenerToken.ForSuggestion(suggestion.Endpoint)", origin, StringComparison.Ordinal);

        var overview = AppSourceTree.CodeWithoutComments("Views/DashboardPage.xaml.cs");
        var token = overview.IndexOf("EditorOpenerToken.TryGetSuggestion(editorOpener, out var suggestion) && FocusSuggestion(suggestion)", StringComparison.Ordinal);
        Assert.True(token > 0);
        Assert.True(token < overview.IndexOf("EditorReturnFocus.TryFocus(this, editorOpener)", StringComparison.Ordinal));
        Assert.True(token > overview.IndexOf("if (ShellPageFocus.GetKeepSidebar(this)) return;", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("10.0.0.5:22")]
    [InlineData("[fe80::1]:2222")]
    public void M3_TheSuggestionToken_RoundTrips_AndAPlainNameIsNotAToken(string endpoint)
    {
        Assert.True(EditorOpenerToken.TryGetSuggestion(EditorOpenerToken.ForSuggestion(endpoint), out var back));
        Assert.Equal(endpoint, back);
        Assert.False(EditorOpenerToken.TryGetSuggestion("OverviewAddButton", out _));
        Assert.False(EditorOpenerToken.TryGetSuggestion("suggestion:", out _));
        Assert.False(EditorOpenerToken.TryGetSuggestion(null, out _));
    }

    [Fact]
    public void TheTestDialog_StartsOnlyThroughTheController_AndLivesInTheSameLayer()
    {
        var page = Page;
        Assert.Contains("_controller.StartTestAsync(ServerForm.CaptureSecret)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("TestConnectionAsync(", page, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentDialog", page, StringComparison.Ordinal);

        var xaml = AppSourceTree.LoadXaml("Views/ServerEditorPage.xaml");
        var surface = xaml.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "DialogSurface");
        var panels = surface.Descendants().Where(e => e.Name.LocalName is "ServerEditorTestPanel" or "ServerEditorTrustPanel" or "SshConfigImportPanel").ToList();
        Assert.Equal(3, panels.Count); // the test, the trust prompt it meets, and the import: ONE layer
        Assert.Equal("True", (string?)surface.Attribute(XName.Get("AutomationProperties.IsDialog")));
        Assert.Equal("Cycle", (string?)surface.Attribute("TabFocusNavigation"));
    }

    /// <summary>
    /// UI.7C QA finding (B-19): width states attached to a child of the page never fired (at 560 the editor kept its wide
    /// header). They live on the page's root element - the editor and the Detail it restructured for the saved toast.
    /// </summary>
    [Theory]
    [InlineData("Views/ServerEditorPage.xaml", "EditorHost")]
    [InlineData("Views/ServerDetailPage.xaml", "DetailHost")]
    public void TheWidthStates_LiveOnThePagesRootElement(string file, string root)
    {
        var page = AppSourceTree.LoadXaml(file).Root!;
        var content = page.Elements().Single(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal));
        Assert.Equal(root, (string?)content.Attribute(AppSourceTree.Xaml + "Name"));
        Assert.Contains(content.Elements(), e => e.Name.LocalName == "VisualStateManager.VisualStateGroups");
        Assert.Single(page.Descendants().Where(e => e.Name.LocalName == "VisualStateManager.VisualStateGroups"
            && e.Descendants().Any(d => (string?)d.Attribute(AppSourceTree.Xaml + "Name") == "WidthStates")));
    }

    [Fact]
    public void TheFormHasNoInlineChecklistOrValidationInfoBar_AnyMore()
    {
        var form = AppSourceTree.LoadXaml("Controls/ServerFormControl.xaml");
        Assert.DoesNotContain(form.Descendants(), e => e.Name.LocalName == "InfoBar");
        Assert.DoesNotContain(form.Descendants(), e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "ChecklistPanel");
        Assert.DoesNotContain("ConnectionChecklist", form.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheValidationSummary_IsTheAssertiveH1Subtitle()
    {
        var xaml = AppSourceTree.LoadXaml("Views/ServerEditorPage.xaml");
        var subtitle = xaml.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "EditorSubtitle");
        Assert.Equal("Assertive", (string?)subtitle.Attribute(XName.Get("AutomationProperties.LiveSetting")));
        Assert.Contains("\"ServerEditorValidationSummary\"", Page, StringComparison.Ordinal);
        Assert.Contains("ServerForm.FocusField(", Page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Styles/Components/Sa.Forms.xaml", "SaPasswordBoxErrorStyle", "SaPasswordBoxStyle")]
    [InlineData("Styles/Components/Sa.Buttons.xaml", "SaPickerButtonErrorStyle", "SaPickerButtonStyle")]
    [InlineData("Styles/Components/Sa.Forms.xaml", "SaTextFieldErrorStyle", "SaTextFieldStyle")]
    public void EveryInvalidField_UsesTheSame15DangerBorder(string file, string key, string basedOn)
    {
        var style = AppSourceTree.LoadXaml(file).Descendants()
            .Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute(AppSourceTree.Xaml + "Key") == key);
        Assert.Equal($"{{StaticResource {basedOn}}}", (string?)style.Attribute("BasedOn"));
        var setters = style.Elements().Where(e => e.Name.LocalName == "Setter")
            .ToDictionary(e => (string)e.Attribute("Property")!, e => (string)e.Attribute("Value")!);
        Assert.Equal("{ThemeResource SaErrorBrush}", setters["BorderBrush"]);
        Assert.Equal("{StaticResource SaErrorBorderThickness}", setters["BorderThickness"]);
    }

    [Fact]
    public void TheFieldErrors_AreDrawnByTheFormOnly_WithTheErrorStyles()
    {
        var form = AppSourceTree.CodeWithoutComments("Controls/ServerFormControl.xaml.cs");
        foreach (var style in new[] { "SaTextFieldErrorStyle", "SaPasswordBoxErrorStyle", "SaPickerButtonErrorStyle" })
        {
            Assert.Contains($"\"{style}\"", form, StringComparison.Ordinal);
        }

        Assert.Contains("field.ErrorText = message;", form, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTestPanel_GlyphsAreSaIconsAndAProgressRing_NotTextGlyphs()
    {
        var panel = AppSourceTree.LoadXaml("Views/ServerEditorTestPanel.xaml").ToString();
        foreach (var icon in new[] { "SaIconTick02Data", "SaIconCancel01Data", "SaIconAlert02Data" })
        {
            Assert.Contains(icon, panel, StringComparison.Ordinal);
        }

        Assert.Contains("<ProgressRing", panel, StringComparison.Ordinal);
        foreach (var glyph in new[] { "✓", "◌", "○", "×", "&#xE73E;", "&#xEA39;", "FontIcon" })
        {
            Assert.DoesNotContain(glyph, panel, StringComparison.Ordinal);
        }
    }
}
