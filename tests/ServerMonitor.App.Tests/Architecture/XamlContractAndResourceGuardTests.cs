using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.1 regression guards for the presentation rebuild (docs/ui/ui0-figma-audit.md §5 #10, §6.9, §9).
/// <list type="number">
/// <item>XAML contract: every named element that code-behind or a runtime <c>ElementName</c> binding depends on
/// stays declared in the XAML file it is expected in. A rebuild that moves or renames one must migrate the
/// code-behind in the same PR.</item>
/// <item>Resource keys: every <c>{StaticResource}</c>/<c>{ThemeResource}</c>/<c>ResourceKey</c> used by app XAML and
/// every <c>Application.Current.Resources[...]</c> key used by app C# resolves to a key the app defines
/// (App.xaml, any dictionary under Styles/**, or the file's own local resources) or to the documented WinUI
/// platform allowlist below.</item>
/// </list>
/// Styles/** is enumerated generically, so new token dictionaries (e.g. Styles/Tokens/*.xaml) are picked up
/// without editing this test.
/// </summary>
public sealed partial class XamlContractAndResourceGuardTests
{
    /// <summary>
    /// The named-element contract, captured from today's consumers. Each name is referenced by the paired
    /// *.xaml.cs (a generated field) unless noted. Test-only or harness-only consumers: none exist today (no
    /// FindName/GetTemplateChild/AutomationId lookups anywhere in src/ or tests/); add them here if introduced.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> Contract = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        // Shell: standard/compact presentations, title bar, navigation frame, modal host, theme background.
        // CompactRoot is also the target of a runtime {Binding ElementName=CompactRoot} in MainWindow.xaml.
        ["MainWindow.xaml"] =
        [
            "RootLayout", "StandardRoot", "AppTitleBar", "ContentFrame", "ModalOverlayHost",
            "CompactRoot", "CompactCaptionColumn", "CompactDragRegion", "CompactBody"
        ],
        ["Views/DashboardPage.xaml"] = ["ServersItemsControl"],
        ["Views/SettingsPage.xaml"] = ["BackupStatusBar", "BackgroundSection"],
        // UI.3: adaptive VisualState setters reflow these (no code-behind dependency).
        ["Views/HistoryPage.xaml"] = ["RangeLast30Days", "UnavailableState", "EmptyState", "PageRoot", "ControlsRow", "ServerSelector", "RangeTrack", "CpuChart", "MemoryChart", "DiskChart", "RetentionText"],
        ["Views/WorkloadsPage.xaml"] = ["PageHost", "PageScroll", "PageRoot", "QueryRow", "SearchBox", "FilterTrack", "ServicesCard", "CardsGrid", "CardsColumn2", "CardsRow1", "CardsRow2", "ContainersScroll", "ServicesScroll",
            "DockerNoResultsText", "DockerNotInstalledState", "DockerPermissionState", "DockerUnavailableState", "DockerErrorState", "DockerEmptyState",
            "ServicesNoResultsText", "ServicesUnsupportedState", "ServicesUnavailableState", "ServicesErrorState", "ServicesEmptyState",
            "UnavailableState", "NothingState", "NoResultsState"],
        // UI.4: "Limpar pesquisa" returns focus to the search box (code-behind FocusAfterAction).
        ["Views/ServersPage.xaml"] = ["SearchBox"],
        ["Views/BackupCreateDialog.xaml"] = ["PassphraseBox", "ConfirmationBox"],
        ["Views/RestoreOpenDialog.xaml"] = ["PassphraseBox"],
        ["Views/AddServerDialog.xaml"] = ["ServerForm"],
        ["Views/EditServerDialog.xaml"] = ["ServerForm"],
        ["Controls/ServerEditorModal.xaml"] = ["ServerForm", "ModalTitleText", "PrimaryActionButton", "CancelActionButton"],
        ["Controls/ServerFullCard.xaml"] = ["FocusRing"],
        ["Controls/ServerFormControl.xaml"] =
        [
            "NameField", "PrepHelpLink", "PrepHelpContent", "PrepKeygenText", "PrepCopyKeygenButton",
            "PrepCopyKeyText", "PrepCopyKeyButton", "PrepPlaceholderNote", "LocalKeySelector", "PassphraseField",
            "PasswordField", "JumpLocalKeySelector", "JumpPassphraseField", "JumpPasswordField", "ChecklistPanel",
            "UnknownHostHeading"
        ],
        // HistoryChart parts: the chart draws into these by name.
        ["Controls/HistoryChart.xaml"] = ["RootGrid", "PlotHost", "GridCanvas", "PlotCanvas", "YAxisCanvas", "XAxisCanvas"],
        // UI.2 primitive templates: names resolved at runtime by VisualState setters (G-3).
        ["Styles/Components/Sa.Primitives.xaml"] =
        [
            "PART_Dot", "PART_Label", "PART_Path", "PART_Header", "PART_Helper", "PART_Error", "PART_PasswordBox", "PART_RevealButton",
            // S6
            "RootGrid", "KeyColumn", "PART_Key", "PART_Value", "PART_Fill", "PART_Track", "PART_Text", "PART_Icon", "PART_Chevron",
            "PART_Detail", "PART_Trailing", "PART_InfoLayout", "PART_ErrorLayout", "PART_CloseButton", "PART_Parent"
        ],
        // UI.2 S4 control templates: names targeted by VisualState setters / storyboards (G-3).
        ["Styles/Components/Sa.Buttons.xaml"] = ["RootGrid", "StateOverlay", "ContentPresenter"],
        ["Styles/Components/Sa.Forms.xaml"] =
        [
            "RootGrid", "HoverOverlay", "FocusRing", "Shell", "Highlight", "StateOverlay", "ContentPresenter", "Box",
            "CheckGlyph", "IndeterminateGlyph", "SwitchAreaGrid", "SwitchKnobBounds", "KnobTranslateTransform"
        ],
        ["Styles/Components/Sa.Navigation.xaml"] = ["RootGrid", "Shell", "Highlight", "StateOverlay", "ContentPresenter"],
        // UI.2 R1 (Prism MF-3): the Sa ContentDialog template keeps the Fluent PART names - ContentDialog's own code resolves
        // them (GetTemplateChild) and the VisualState setters target them; SaDialog focuses PrimaryButton/CloseButton by name.
        ["Styles/Components/Sa.Dialogs.xaml"] =
        [
            "Container", "LayoutRoot", "SmokeLayerBackground", "BackgroundElement", "ScaleTransform", "DialogSpace",
            "ContentScrollViewer", "Title", "Content", "CommandSpace", "PrimaryButton", "SecondaryButton", "CloseButton"
        ],
        // UI.2 Debug-only component gallery (Qa/Gallery/**, excluded from Release).
        ["Qa/Gallery/QaGalleryWindow.xaml"] =
        [
            "GalleryRoot", "DarkThemeOption", "LightThemeOption", "HcSimThemeOption", "HcBannerText", "PageList",
            "ContentHost", "SimulationHost", "PageFrame"
        ],
        ["Qa/Gallery/QaTokenProbePage.xaml"] = ["DefaultStyleProbe", "DotOnlyStyleProbe", "StrokeProbe16", "StrokeProbe20", "StrokeProbe24", "StrokeProbe48", "RevealNameProbe",
            "IconButtonNameProbe", "ToastNameProbe"],
        ["Qa/Gallery/QaColorsPage.xaml"] = ["PrimitiveSwatches"],
        // DialogInitialFocus / DialogResult are also read by AutomationId by tools/qa/ui2-dialog-probe.ps1.
        ["Qa/Gallery/QaPopupDialogPage.xaml"] = ["Description", "InitialFocusText", "DefaultButtonText", "ResultText"],
        ["Qa/Gallery/QaMaterialsPage.xaml"] = ["FallbackToggle", "GlassSample", "GlassFallbackSample"],
        // Storyboard.TargetName lanes (G-3) and the C#-path lanes driven by the code-behind.
        ["Qa/Gallery/QaMotionPage.xaml"] =
        [
            "XamlFadeBox", "XamlFastBox", "XamlNormalBox", "XamlSlowBox", "XamlFocusBox", "XamlReducedBox",
            "CodeFadeBox", "CodeFastBox", "CodeNormalBox", "CodeSlowBox", "CodeFocusBox", "AnimationsText", "TokenValuesText"
        ]
    };

    /// <summary>
    /// WinUI platform keys the app consumes but does not define, supplied by <c>XamlControlsResources</c> /
    /// the framework. Captured from today's usage (UI.1). Add a key here only when it is genuinely a platform
    /// resource; an app-owned key belongs in a Styles/** dictionary instead.
    /// </summary>
    private static readonly HashSet<string> PlatformResourceKeys = new(StringComparer.Ordinal)
    {
        // WinUI ContentDialog default style (XamlControlsResources); BasedOn target in Styles/Controls.xaml.
        "DefaultContentDialogStyle",

        // High-contrast system colour brushes (framework HighContrast theme), used by the HC theme dictionary
        // in Styles/DesignTokens.xaml.
        "SystemColorGrayTextBrush",
        "SystemColorHighlightBrush",
        "SystemColorHighlightTextBrush",
        "SystemColorWindowBrush",
        "SystemColorWindowTextBrush",

        // High-contrast system colours (framework HighContrast theme), used via ThemeResource by the HC theme
        // dictionaries in Styles/Tokens/Color.Semantic.xaml and Styles/Tokens/Elevation.xaml.
        "SystemColorGrayTextColor",
        "SystemColorHighlightColor",
        "SystemColorHighlightTextColor",
        "SystemColorWindowColor",
        "SystemColorWindowTextColor",
        // UI.2 R1 (Prism MF-4): HC rest fill/border of secondary controls - the framework's ButtonFace/ButtonText.
        "SystemColorButtonFaceColor",
        "SystemColorButtonTextColor"
    };

    /// <summary>True for a documented WinUI platform key (shared with the UI.2 component-layer guards).</summary>
    internal static bool IsPlatformKey(string key) => PlatformResourceKeys.Contains(key);

    [Fact]
    public void NamedElementsUsedByCodeRemainInTheirXamlFiles()
    {
        var failures = new List<string>();
        foreach (var (file, names) in Contract)
        {
            if (!File.Exists(AppSourceTree.Full(file)) && !File.Exists(AppSourceTree.Full(file + ".cs")))
            {
                // The view and its code-behind were removed together: no consumer is left to break.
                continue;
            }

            if (!File.Exists(AppSourceTree.Full(file)))
            {
                failures.Add($"{file} no longer exists but code depends on {string.Join(", ", names)}; migrate the code-behind in the same PR");
                continue;
            }

            var declared = DeclaredNames(AppSourceTree.LoadXaml(file));
            failures.AddRange(names
                .Where(name => !declared.Contains(name))
                .Select(name => $"x:Name=\"{name}\" must remain in {file}; migrate the code-behind in the same PR"));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Keeps the contract honest: a new dependency on a named element must be inventoried. Consumers are the
    /// paired code-behind, runtime <c>ElementName</c> bindings, and (UI.2 G-3) every other name resolved at
    /// runtime: <c>Storyboard.TargetName</c>/<c>TargetName</c> and VisualState <c>Setter Target="X.Prop"</c> in the
    /// same file, and <c>FindName("X")</c>/<c>GetTemplateChild("X")</c>/<c>[TemplatePart(Name = "X")]</c> literals in
    /// any app C# file.
    /// </summary>
    [Fact]
    public void ContractInventoryCoversEveryNameCodeBehindOrElementNameDependsOn()
    {
        var codeLookups = AppSourceTree.Files(".cs")
            .SelectMany(file => RuntimeNameLookups(AppSourceTree.CodeWithoutComments(file)))
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var xaml in AppSourceTree.Files(".xaml"))
        {
            var document = AppSourceTree.LoadXaml(xaml);
            var declared = DeclaredNames(document);
            if (declared.Count == 0)
            {
                continue;
            }

            var used = new HashSet<string>(StringComparer.Ordinal);
            var codeBehind = xaml + ".cs";
            if (File.Exists(AppSourceTree.Full(codeBehind)))
            {
                var code = AppSourceTree.CodeWithoutComments(codeBehind);
                used.UnionWith(declared.Where(name => Regex.IsMatch(code, $@"\b{Regex.Escape(name)}\b")));
            }

            used.UnionWith(document.Descendants().Attributes()
                .SelectMany(attribute => ElementNameReference().Matches(attribute.Value))
                .Select(match => match.Groups[1].Value)
                .Where(declared.Contains));

            used.UnionWith(document.Descendants().Attributes()
                .Where(attribute => attribute.Name.LocalName is "Storyboard.TargetName" or "TargetName")
                .Select(attribute => attribute.Value)
                .Where(declared.Contains));

            used.UnionWith(document.Descendants()
                .Where(element => element.Name.LocalName == "Setter")
                .Select(element => (string?)element.Attribute("Target"))
                .OfType<string>()
                .Select(target => target.Split('.')[0])
                .Where(declared.Contains));

            used.UnionWith(codeLookups.Where(declared.Contains));

            var inventoried = Contract.TryGetValue(xaml, out var names) ? names.ToHashSet(StringComparer.Ordinal) : [];
            missing.AddRange(used.Where(name => !inventoried.Contains(name)).OrderBy(n => n, StringComparer.Ordinal)
                .Select(name => $"{xaml}: {name}"));
        }

        Assert.True(missing.Count == 0,
            "Code depends on these named elements but they are not in the XAML contract inventory; add them to " +
            $"{nameof(Contract)}:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
    }

    [Fact]
    public void XamlAndCodeResourceReferencesResolveToAppDictionariesOrDocumentedPlatformKeys()
    {
        var documents = AppSourceTree.Files(".xaml").ToDictionary(file => file, AppSourceTree.LoadXaml, StringComparer.Ordinal);
        var appWide = AppWideKeys(documents);

        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (file, document) in documents)
        {
            var local = DefinedKeys(document);
            foreach (var (_, key) in AppSourceTree.XamlResourceReferences(document))
            {
                if (!appWide.Contains(key) && !local.Contains(key) && !PlatformResourceKeys.Contains(key))
                {
                    unresolved.Add($"{file}: {key}");
                }
            }
        }

        foreach (var file in AppSourceTree.Files(".cs"))
        {
            foreach (var key in CodeResourceReferences(AppSourceTree.CodeWithoutComments(file)))
            {
                if (!appWide.Contains(key) && !PlatformResourceKeys.Contains(key))
                {
                    unresolved.Add($"{file}: {key}");
                }
            }
        }

        Assert.True(unresolved.Count == 0,
            "Unresolved resource keys (define them in a Styles/** dictionary merged by App.xaml, or - only for a " +
            $"genuine WinUI platform key - add them to {nameof(PlatformResourceKeys)}):{Environment.NewLine}" +
            string.Join(Environment.NewLine, unresolved));
    }

    /// <summary>
    /// UI.1 review G-6: app-wide set membership cannot see a key defined for one theme only. In every
    /// ThemeDictionaries block under Styles/Tokens/**, Dark, Light and HighContrast must all exist and define the
    /// identical key set, so a token never silently falls back (or fails) in one theme. Static check only; the
    /// runtime resolution proof stays a UI.2 gate (ui1-tokens.md §7.2 #1).
    /// </summary>
    [Fact]
    public void TokenThemeDictionariesDefineTheSameKeysInDarkLightAndHighContrast()
    {
        string[] themes = ["Dark", "Light", "HighContrast"];
        var failures = new List<string>();
        var blocks = 0;
        // UI.2 T-4: the component layer (Styles/Components/**, e.g. the F-3 accent-neutral scope) obeys the same parity.
        foreach (var file in AppSourceTree.Files(".xaml").Where(f => f.StartsWith("Styles/Tokens/", StringComparison.OrdinalIgnoreCase)
                     || f.StartsWith("Styles/Components/", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var block in AppSourceTree.LoadXaml(file).Descendants().Where(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries"))
            {
                blocks++;
                var byTheme = block.Elements()
                    .Where(e => e.Attribute(AppSourceTree.Xaml + "Key") is not null)
                    .ToDictionary(
                        e => e.Attribute(AppSourceTree.Xaml + "Key")!.Value,
                        e => e.Elements().Select(c => c.Attribute(AppSourceTree.Xaml + "Key")?.Value).OfType<string>().ToHashSet(StringComparer.Ordinal),
                        StringComparer.Ordinal);

                var missingThemes = themes.Where(theme => !byTheme.ContainsKey(theme)).ToList();
                if (missingThemes.Count > 0)
                {
                    failures.Add($"{file}: ThemeDictionaries lacks {string.Join(", ", missingThemes)}");
                    continue;
                }

                var all = themes.SelectMany(theme => byTheme[theme]).ToHashSet(StringComparer.Ordinal);
                foreach (var theme in themes)
                {
                    failures.AddRange(all.Except(byTheme[theme]).OrderBy(k => k, StringComparer.Ordinal)
                        .Select(key => $"{file}: {key} is not defined for {theme}"));
                }
            }
        }

        Assert.True(blocks > 0, "No ThemeDictionaries found under Styles/Tokens/** - the parity guard would be vacuous.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void PlatformAllowlistNeverShadowsAnAppDefinedKey()
    {
        var documents = AppSourceTree.Files(".xaml").ToDictionary(file => file, AppSourceTree.LoadXaml, StringComparer.Ordinal);
        var appWide = AppWideKeys(documents);

        // A key the app now defines itself must leave the allowlist, so the allowlist stays platform-only.
        Assert.Empty(PlatformResourceKeys.Where(appWide.Contains).OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>
    /// resw pin: the server editor modal titles/buttons come from the six AddServerDialog.* / EditServerDialog.*
    /// resw entries, not from the dialog views. The views may be deleted by the rebuild; these keys must survive
    /// in every shipped culture and stay referenced by Controls/ServerEditorModal.xaml.cs.
    /// </summary>
    [Fact]
    public void ServerEditorModalKeysExistInEveryCultureAndAreReferencedByTheModal()
    {
        string[] cultures = ["pt-PT", "pt-BR", "en-US"];
        var keys = new[] { "AddServerDialog", "EditServerDialog" }
            .SelectMany(prefix => new[] { "Title", "PrimaryButtonText", "CloseButtonText" }.Select(property => (prefix, property)))
            .ToList();
        var modal = File.ReadAllText(AppSourceTree.Full("Controls/ServerEditorModal.xaml.cs"));

        var failures = new List<string>();
        foreach (var culture in cultures)
        {
            var entries = XDocument.Load(AppSourceTree.Full($"Resources/{culture}/Resources.resw"))
                .Root!.Elements("data")
                .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value"), StringComparer.Ordinal);
            failures.AddRange(keys
                .Where(key => string.IsNullOrWhiteSpace(entries.GetValueOrDefault($"{key.prefix}.{key.property}")))
                .Select(key => $"Resources/{culture}/Resources.resw is missing {key.prefix}.{key.property}"));
        }

        failures.AddRange(keys
            .Where(key => !modal.Contains($"\"{key.prefix}/{key.property}\"", StringComparison.Ordinal))
            .Select(key => $"Controls/ServerEditorModal.xaml.cs no longer reads \"{key.prefix}/{key.property}\""));

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>App-wide scope: App.xaml plus every dictionary under Styles/** (generic, so new token files count).</summary>
    private static HashSet<string> AppWideKeys(IReadOnlyDictionary<string, XDocument> documents) =>
        documents
            .Where(pair => pair.Key == "App.xaml" || AppSourceTree.IsUnderStyles(pair.Key))
            .SelectMany(pair => DefinedKeys(pair.Value))
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> DeclaredNames(XDocument document) =>
        document.Descendants().Attributes(AppSourceTree.Xaml + "Name").Select(a => a.Value).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> DefinedKeys(XDocument document) =>
        document.Descendants().Attributes(AppSourceTree.Xaml + "Key").Select(a => a.Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// <c>Resources["Key"]</c> reads (an assignment <c>dialog.Resources["X"] = ...</c> defines a local override and is
    /// not a lookup), plus - in any file that indexes <c>Resources[variable]</c> - every string literal shaped like a
    /// resource key (...Brush / ...Style), which is how the converters select their keys.
    /// </summary>
    private static IEnumerable<string> CodeResourceReferences(string code)
    {
        foreach (Match match in LiteralResourceRead().Matches(code))
        {
            yield return match.Groups[1].Value;
        }

        if (VariableResourceRead().IsMatch(code))
        {
            foreach (Match match in ResourceKeyShapedLiteral().Matches(code))
            {
                yield return match.Groups[1].Value;
            }
        }
    }

    /// <summary>Names looked up at runtime: a literal, or a <c>const string</c> declared in the same file (G-3).</summary>
    internal static IEnumerable<string> RuntimeNameLookups(string code)
    {
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match constant in ConstString().Matches(code))
        {
            constants.TryAdd(constant.Groups[1].Value, constant.Groups[2].Value);
        }

        foreach (Match match in RuntimeNameLookup().Matches(code))
        {
            if (match.Groups["literal"].Success)
            {
                yield return match.Groups["literal"].Value;
            }
            else if (constants.TryGetValue(match.Groups["constant"].Value, out var value))
            {
                yield return value;
            }
        }
    }

    [GeneratedRegex(@"\b(?:(?:FindName|GetTemplateChild)\s*\(|TemplatePart\s*\(\s*Name\s*=)\s*(?:""(?<literal>[^""]+)""|(?<constant>[A-Za-z_]\w*))")]
    private static partial Regex RuntimeNameLookup();

    [GeneratedRegex(@"\bconst\s+string\s+([A-Za-z_]\w*)\s*=\s*""([^""]*)""")]
    private static partial Regex ConstString();

    [GeneratedRegex(@"ElementName\s*=\s*([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex ElementNameReference();

    [GeneratedRegex(@"Resources\[""([^""]+)""\](?!\s*=[^=])")]
    private static partial Regex LiteralResourceRead();

    [GeneratedRegex(@"Resources\[[A-Za-z_][A-Za-z0-9_]*\]")]
    private static partial Regex VariableResourceRead();

    [GeneratedRegex(@"""([A-Z][A-Za-z0-9]*(?:Brush|Style))""")]
    private static partial Regex ResourceKeyShapedLiteral();
}
