using System.Xml.Linq;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Tests.ViewModels;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.SshConfig;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.7 final wave fix c1 (Prism F-1…F-10, Cortex n-1): the editor's Figma-fidelity fixes, held on the geometry rules the
/// page applies (not only on XAML text, P-023), on the shipped copy of the three cultures and on the styles' structure.
/// </summary>
public sealed class Ui7FinalC1Tests
{
    private static readonly XNamespace X = AppSourceTree.Xaml;

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static XElement Keyed(XDocument document, string key) =>
        document.Descendants().Single(e => (string?)e.Attribute(X + "Key") == key);

    private static Thickness CompactPagePadding()
    {
        var value = Keyed(AppSourceTree.LoadXaml("Styles/Tokens/Spacing.xaml"), "SaPagePaddingCompact").Value.Split(',').Select(double.Parse).ToArray();
        return new Thickness(value[0], value[1], value[2], value[3]);
    }

    // ---- F-1: the layer is 640 (test / trust) or 720 (import), whatever the state says; a narrow page still fits it. ----

    [Theory]
    [InlineData(ServerEditorLayer.Test, 640)]
    [InlineData(ServerEditorLayer.Trust, 640)]
    [InlineData(ServerEditorLayer.Import, 720)]
    public void F1_TheDialogWidth_IsFixedPerLayer(ServerEditorLayer layer, double width) =>
        Assert.Equal(width, ServerEditorPage.DialogWidth(layer));

    [Theory]
    [InlineData(1440)]
    [InlineData(1040)]
    [InlineData(900)]
    [InlineData(700)]
    [InlineData(640)]
    [InlineData(560)]
    public void F1_TheArrangedWidth_IsFigmaWhenItFits_AndNeverWiderThanThePageMinusTheCompactMargins(double hostWidth)
    {
        var margin = CompactPagePadding();
        foreach (var layer in new[] { ServerEditorLayer.Test, ServerEditorLayer.Trust, ServerEditorLayer.Import })
        {
            // WinUI: MaxWidth wins over Width.
            var arranged = Math.Min(ServerEditorPage.DialogWidth(layer), ServerEditorPage.DialogMaxWidth(hostWidth, margin));
            Assert.True(arranged + margin.Left + margin.Right <= hostWidth, $"{layer} at {hostWidth}: {arranged} does not fit");
            if (hostWidth >= ServerEditorPage.DialogWidth(layer) + margin.Left + margin.Right)
            {
                Assert.Equal(ServerEditorPage.DialogWidth(layer), arranged);
            }
        }
    }

    [Fact]
    public void F1_ThePageSetsTheWidthOnlyFromTheLayer_AndTracksItsOwnWidthForTheMaximum()
    {
        var page = AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, @"DialogSurface\.Width\s*="));
        Assert.Contains("DialogSurface.Width = DialogWidth(layer);", page, StringComparison.Ordinal);
        Assert.Contains("DialogSurface.MaxWidth = DialogMaxWidth(EditorHost.ActualWidth, DialogSurface.Margin);", page, StringComparison.Ordinal);
        Assert.Contains("EditorHost.SizeChanged += (_, _) => UpdateDialogMaxWidth();", page, StringComparison.Ordinal);

        var surface = Named(AppSourceTree.LoadXaml("Views/ServerEditorPage.xaml"), "DialogSurface");
        Assert.Null(surface.Attribute("MaxWidth"));
        Assert.Null(surface.Attribute("Width"));
        Assert.Equal("{StaticResource SaPagePaddingCompact}", (string?)surface.Attribute("Margin"));
    }

    // ---- F-2: import rows (surface, compact detail, jump line, key never drawn), counts, selected profile details. ----

    private static SshConfigHostEntry Entry(int? port = 22, string? jump = null) => new()
    {
        Alias = "prod",
        HostName = "192.0.2.10",
        User = "monitor",
        Port = port,
        IdentityFile = @"C:\Users\someone\.ssh\id_ed25519",
        Jump = jump is null ? null : new SshConfigJumpHost { Name = jump, HostName = "198.51.100.7" }
    };

    [Theory]
    [InlineData("pt-PT", 1, 1, "1 disponível · 1 bloqueado")]
    [InlineData("pt-PT", 2, 0, "2 disponíveis · 0 bloqueados")]
    [InlineData("pt-BR", 1, 3, "1 disponível · 3 bloqueados")]
    [InlineData("en-US", 1, 2, "1 available · 2 blocked")]
    public void F2_TheCount_HasItsSingularAndPlural(string culture, int available, int blocked, string expected) =>
        Assert.Equal(expected, SshConfigImportPresentation.CountText(available, blocked, new ResWLocalizationService(culture)));

    [Theory]
    [InlineData("pt-PT", "192.0.2.10 · monitor · porta 22", "Via jump host bastion")]
    [InlineData("pt-BR", "192.0.2.10 · monitor · porta 22", "Via jump host bastion")]
    [InlineData("en-US", "192.0.2.10 · monitor · port 22", "Via jump host bastion")]
    public void F2_TheRowDetail_IsCompact_AndNeverTheKeyPath(string culture, string detail, string jump)
    {
        var localization = new ResWLocalizationService(culture);
        Assert.Equal(detail, SshConfigImportPresentation.Detail(Entry(), localization));
        Assert.DoesNotContain("id_ed25519", SshConfigImportPresentation.Detail(Entry(), localization), StringComparison.Ordinal);
        Assert.Equal(jump, SshConfigImportPresentation.Jump(Entry(jump: "bastion"), localization));
        Assert.Equal(string.Empty, SshConfigImportPresentation.Jump(Entry(), localization));
        // Nothing assumed: a profile without a Port says no port.
        Assert.Equal("192.0.2.10 · monitor", SshConfigImportPresentation.Detail(Entry(port: null), localization));
    }

    [Fact]
    public void F2_TheRowTemplate_IsASurface_WithTheDetailLines_AndTheSelectedProfileDetails()
    {
        var xaml = AppSourceTree.LoadXaml("Views/SshConfigImportPanel.xaml");
        var list = Named(xaml, "HostList");
        Assert.Equal("{StaticResource SaInsetListItemStyle}", (string?)list.Attribute("ItemContainerStyle"));
        var surfaces = AppSourceTree.LoadXaml("Styles/Components/Sa.Surfaces.xaml");
        var container = Keyed(surfaces, "SaInsetListItemStyle");
        string Setter(string property) => (string)container.Elements().Single(e => (string?)e.Attribute("Property") == property).Attribute("Value")!;
        Assert.Equal("ListViewItem", (string?)container.Attribute("TargetType"));
        Assert.Equal("{ThemeResource SaInsetTintedBrush}", Setter("Background"));
        Assert.Equal("{StaticResource SaRadiusListRow}", Setter("CornerRadius"));
        Assert.Equal("13", Keyed(AppSourceTree.LoadXaml("Styles/Tokens/Radius.xaml"), "SaRadiusListRow").Value);
        Assert.Equal("0,0,0,8", Setter("Margin"));
        Assert.Contains(list.Descendants(), e => e.Name.LocalName == "ResourceDictionary"
            && (string?)e.Attribute("Source") == "ms-appx:///Styles/Components/Sa.AccentNeutralScope.xaml");

        var template = list.Descendants().Single(e => e.Name.LocalName == "DataTemplate");
        Assert.DoesNotContain(template.Descendants(), e => ((string?)e.Attribute("Text"))?.Contains("Preview", StringComparison.Ordinal) == true);
        Assert.Single(template.Descendants(), e => (string?)e.Attribute(X + "Name") == "DetailText");
        Assert.Single(template.Descendants(), e => (string?)e.Attribute(X + "Name") == "JumpText");
        foreach (var name in new[] { "DetailsHostValue", "DetailsUserValue", "DetailsKeyValue" })
        {
            Assert.Equal("DetailsPanel", (string?)Named(xaml, name).Ancestors().First(e => e.Name.LocalName == "Border").Attribute(X + "Name"));
        }

        var panel = AppSourceTree.CodeWithoutComments("Views/SshConfigImportPanel.xaml.cs");
        Assert.Contains("DetailsKeyValue.Text = ServerEditorKeyPicker.FileName(keyPath) ?? notSet;", panel, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetHelpText(DetailsKeyValue, keyPath);", panel, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetHelpText(args.ItemContainer,", panel, StringComparison.Ordinal);
        Assert.Contains("SshConfigImportPresentation.CountText(", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void F2_F8_TheNewKeys_ExistInEveryCulture_AndTheOldCountFormatIsGone()
    {
        Ui7Resources.AssertPresentInEveryCulture(
        [
            "ServerEditorImportAvailableCountOne", "ServerEditorImportAvailableCountOther",
            "ServerEditorImportBlockedCountOne", "ServerEditorImportBlockedCountOther",
            "ServerEditorImportRowPortFormat", "ServerEditorImportRowJumpFormat",
            "ServerEditorImportDetailsHost.Text", "ServerEditorImportDetailsUser.Text", "ServerEditorImportDetailsKey.Text",
            "ServerEditorImportDetailsNotSet", "ServerEditorKeyPickerCurrentHelper", "ServerEditorKeyPickerCurrentSshHelper"
        ]);
        foreach (var culture in ResWLocalizationService.Cultures)
        {
            Assert.False(Ui7Resources.Load(culture).ContainsKey("ServerEditorImportCountFormat"), culture);
        }
    }

    // ---- F-3: a focused invalid field keeps its danger edge (the ring over the border takes the error brush). ----

    [Fact]
    public void F3_TheFocusRing_TakesItsBrushFromTheStyle_AndTheErrorStylesGiveItTheErrorBrush()
    {
        var forms = AppSourceTree.LoadXaml("Styles/Components/Sa.Forms.xaml");
        foreach (var template in new[] { "SaTextFieldTemplate", "SaPasswordBoxTemplate" })
        {
            var ring = Keyed(forms, template).Descendants().Single(e => (string?)e.Attribute(X + "Name") == "FocusRing");
            Assert.Equal(
                "{Binding Path=(primitives:SaFieldChrome.FocusRingBrush), RelativeSource={RelativeSource TemplatedParent}}",
                (string?)ring.Attribute("BorderBrush"));
        }

        string Ring(string style) => (string)Keyed(forms, style).Elements()
            .Single(e => (string?)e.Attribute("Property") == "primitives:SaFieldChrome.FocusRingBrush").Attribute("Value")!;
        Assert.Equal("{ThemeResource SaFocusRingBrush}", Ring("SaTextFieldStyle"));
        Assert.Equal("{ThemeResource SaFocusRingBrush}", Ring("SaPasswordBoxStyle"));
        Assert.Equal("{ThemeResource SaErrorBrush}", Ring("SaTextFieldErrorStyle"));
        Assert.Equal("{ThemeResource SaErrorBrush}", Ring("SaPasswordBoxErrorStyle"));
    }

    // ---- F-4: the inline link is neutral text (HC = Hotlight), underlined on hover / press / focus. ----

    [Fact]
    public void F4_TheTextLink_IsNeutral_WithAnUnderlineOnHoverAndFocus()
    {
        var buttons = AppSourceTree.LoadXaml("Styles/Components/Sa.Buttons.xaml");
        var style = Keyed(buttons, "SaTextLinkButtonStyle");
        string Setter(string property) => (string)style.Elements().Single(e => (string?)e.Attribute("Property") == property).Attribute("Value")!;
        Assert.Equal("{ThemeResource SaLinkTextBrush}", Setter("Foreground"));
        Assert.Equal("{StaticResource SaTextLinkButtonTemplate}", Setter("Template"));

        var template = Keyed(buttons, "SaTextLinkButtonTemplate");
        foreach (var state in new[] { "PointerOver", "Pressed", "Focused" })
        {
            var setters = template.Descendants().Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(X + "Name") == state)
                .Descendants().Where(e => e.Name.LocalName == "Setter").ToList();
            Assert.Contains(setters, e => ((string?)e.Attribute("Target"))?.EndsWith("Underline.Opacity", StringComparison.Ordinal) == true
                && (string?)e.Attribute("Value") == "1");
        }

        var colors = AppSourceTree.LoadXaml("Styles/Tokens/Color.Semantic.xaml");
        string Link(string theme) => (string)colors.Descendants()
            .Single(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(X + "Key") == theme)
            .Elements().Single(e => (string?)e.Attribute(X + "Key") == "SaLinkTextBrush").Attribute("Color")!;
        Assert.Equal("{StaticResource SaColorTextDark}", Link("Dark"));
        Assert.Equal("{StaticResource SaColorTextLight}", Link("Light"));
        Assert.Equal("{ThemeResource SystemColorHotlightColor}", Link("HighContrast"));
    }

    // ---- F-5 / F-10: the prep link has its own row; side by side the cards keep 384. ----

    [Fact]
    public void F5_ThePrepLink_IsNotInTheAuthTitleRow_ButRightUnderTheSubtitle()
    {
        var form = AppSourceTree.LoadXaml("Controls/ServerFormControl.xaml");
        var titleRow = Named(form, "AuthTitle").Parent!;
        Assert.DoesNotContain(titleRow.Descendants(), e => (string?)e.Attribute(X + "Name") == "PrepHelpLink");
        Assert.Equal(2, titleRow.Descendants().Count(e => e.Name.LocalName == "ColumnDefinition"));

        var subtitle = Named(form, "AuthSubtitle");
        var next = subtitle.ElementsAfterSelf().First();
        Assert.Contains(next.DescendantsAndSelf(), e => (string?)e.Attribute(X + "Name") == "PrepHelpLink");
    }

    [Fact]
    public void F10_SideBySide_TheTwoCardsKeepFigmasHeight_StackedTheyHugTheirContent()
    {
        var form = AppSourceTree.LoadXaml("Controls/ServerFormControl.xaml");
        List<(string?, string?)> Setters(string state) => form.Descendants()
            .Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(X + "Name") == state)
            .Descendants().Where(e => e.Name.LocalName == "Setter")
            .Select(e => ((string?)e.Attribute("Target"), (string?)e.Attribute("Value"))).ToList();
        Assert.Contains(("IdentityCard.MinHeight", "384"), Setters("Wide"));
        Assert.Contains(("AuthCard.MinHeight", "384"), Setters("Wide"));
        Assert.DoesNotContain(Setters("Stacked"), setter => setter.Item1?.EndsWith("MinHeight", StringComparison.Ordinal) == true);
        Assert.Null(Named(form, "IdentityCard").Attribute("MinHeight"));
        Assert.Null(Named(form, "AuthCard").Attribute("MinHeight"));
    }

    // ---- F-6 / F-9: collapsed notices take no spacing; "Abrir servidor" is Figma's 220. ----

    [Fact]
    public void F6_TheNoticesAndTheActionBar_ShareOneStackPanel_SoACollapsedNoticeAddsNoSpacing()
    {
        var page = AppSourceTree.LoadXaml("Views/ServerEditorPage.xaml");
        var area = Named(page, "ActionArea");
        Assert.Equal("StackPanel", area.Name.LocalName);
        Assert.Equal("{StaticResource SaSpace16}", (string?)area.Attribute("Spacing"));
        Assert.Equal(
            ["DuplicateNotice", "SaveFailedNotice", "ActionBar"],
            area.Elements().Select(e => (string)e.Attribute(X + "Name")!).ToArray());

        var root = Named(page, "PageRoot");
        var rows = root.Elements().Single(e => e.Name.LocalName == "Grid.RowDefinitions").Elements().Count();
        Assert.Equal(4, rows); // header · form · notices + actions · credential note
        Assert.All(root.Elements().Where(e => !e.Name.LocalName.Contains('.')),
            child => Assert.True(int.Parse((string?)child.Attribute("Grid.Row") ?? "0") < rows));
        Assert.Equal("220", (string?)Named(page, "OpenDuplicateButton").Attribute("MinWidth"));
    }

    // ---- F-7: one vocabulary in the editor family (jump host / host), "tu" in pt-PT. ----

    [Theory]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public void F7_TheEditorFamily_SaysJumpHost_NeverTheTranslatedTerm(string culture)
    {
        string[] family = ["ConnectionError", "ConnectionHint", "SshConfig", "HostKeySubject", "ServerPrep", "ServerEditor", "ServerForm"];
        var offenders = Ui7Resources.Load(culture)
            .Where(entry => family.Any(prefix => entry.Key.StartsWith(prefix, StringComparison.Ordinal)))
            .Where(entry => entry.Value.Contains("anfitri", StringComparison.OrdinalIgnoreCase)
                || entry.Value.Contains("host de salto", StringComparison.OrdinalIgnoreCase)
                || entry.Value.Contains("host-de-salto", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void F7_PtPt_SpeaksTu_InTheCredentialAndJumpMessages()
    {
        var pt = Ui7Resources.Load("pt-PT");
        Assert.Equal("A credencial guardada não está disponível. Volta a introduzi-la.", pt["ConnectionErrorCredentialUnavailable"]);
        foreach (var key in new[] { "ConnectionErrorJumpCredentialUnavailable", "ConnectionErrorJumpHostKeyUnknown", "ConnectionErrorLocalTunnelFailed",
                     "SshConfigHostAmbiguousFormat", "SshConfigImportAppliedFormat", "SshConfigImportNothingFilledHostDiffersFormat" })
        {
            foreach (var formal in new[] { "Introduza", "Ligue-se", "confirme", "Tente", "escolha", "tinha introduzido", "introduziu", "Os seus" })
            {
                Assert.DoesNotContain(formal, pt[key], StringComparison.Ordinal);
            }
        }
    }

    // ---- F-8: Edit with the saved key still chosen says it is the current one. ----

    private const string Saved = @"C:\Users\someone\.ssh\id_ed25519";

    [Theory]
    [InlineData(true, Saved, Saved, true, "", "Chave privada atual · pasta .ssh deste dispositivo")]
    [InlineData(true, Saved, Saved, false, "", "Chave privada atual")]
    [InlineData(true, @"C:\keys\other", Saved, true, "", "Seleciona id_ed25519, id_rsa ou outra chave compatível.")]
    [InlineData(false, Saved, null, true, "", "Seleciona id_ed25519, id_rsa ou outra chave compatível.")]
    [InlineData(true, "", Saved, false, "", "Seleciona id_ed25519, id_rsa ou outra chave compatível.")]
    [InlineData(true, Saved, Saved, true, "Chave encontrada em .ssh", "Chave encontrada em .ssh")]
    public void F8_TheKeyHelper_SaysCurrentOnlyForTheSavedKeyInEdit(
        bool isEdit, string path, string? savedPath, bool discovered, string hint, string expected) =>
        Assert.Equal(expected, ServerEditorKeyPicker.Helper(isEdit, path, savedPath, discovered, hint, new ResWLocalizationService("pt-PT")));

    [Fact]
    public void F8_TheFormAsksTheHelper_WithTheSavedKeyThePageGivesIt()
    {
        var form = AppSourceTree.CodeWithoutComments("Controls/ServerFormControl.xaml.cs");
        Assert.Contains("ServerEditorKeyPicker.Helper(", form, StringComparison.Ordinal);
        Assert.Contains("viewModel.SelectedLocalKeyOption is not null", form, StringComparison.Ordinal);
        Assert.Contains("or nameof(ServerEditorViewModel.SelectedLocalKeyOption)", form, StringComparison.Ordinal);
        Assert.Contains("ServerForm.Configure(_localization, _controller.IsEdit, request.Existing?.PrivateKeyPath);",
            AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs"), StringComparison.Ordinal);
    }

    // ---- Cortex n-1: the saved servers' identities are computed once per visit. ----

    [Fact]
    public void N1_TheIdentitiesAreIndexedOnce_AndFindOnlyCompares()
    {
        var self = new Server { Id = Guid.NewGuid(), Name = "a", Host = "192.0.2.10", Port = 22, Username = "monitor" };
        var other = self with { Id = Guid.NewGuid(), Name = "b" };
        var known = ServerEditorDuplicates.Index([self, other]);
        Assert.All(known, entry => Assert.NotNull(entry.Identity));
        var identity = ServerEditorDuplicates.Of("192.0.2.10", "22", "monitor", false, string.Empty, string.Empty);
        Assert.Same(other, ServerEditorDuplicates.Find(known, identity, self.Id));
        Assert.Null(ServerEditorDuplicates.Find(known, identity! with { Username = "root" }, self.Id));

        var duplicates = AppSourceTree.CodeWithoutComments("ViewModels/ServerEditorDuplicates.cs");
        var find = duplicates[duplicates.IndexOf("public static Server? Find(", StringComparison.Ordinal)..];
        find = find[..find.IndexOf("private static bool TryEndpoint", StringComparison.Ordinal)];
        Assert.DoesNotContain("Of(", find, StringComparison.Ordinal);
        Assert.Contains("_known = ServerEditorDuplicates.Index(known);",
            AppSourceTree.CodeWithoutComments("ViewModels/ServerEditorPageController.cs"), StringComparison.Ordinal);
    }
}
