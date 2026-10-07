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
        ["Controls/OnboardingView.xaml"] = ["DismissButton", "Scroller", "Panel", "HeroBrand", "HeroShield", "HeadingHost", "Heading", "Subtitle", "Benefits", "Benefit0", "Benefit1", "Benefit2", "Principles", "Methods", "Method0", "Method1", "StepNote", "BackButton", "Dot1", "Dot2", "Dot3", "ProgressText", "NextButton"],
        ["Controls/SaSidebar.xaml"] = ["Scroller", "NavGrid", "Brand", "BrandText", "OverviewItem", "OverviewLabel", "ServersItem", "ServersLabel", "HistoryItem", "HistoryLabel", "SettingsItem", "SettingsLabel"],
        ["MainWindow.xaml"] =
        [
            "RootLayout", "WindowBackground", "StandardRoot", "ShellDragRegion", "ShellSurface", "Sidebar", "FirstRunView", "SidebarColumn", "ContentFrame", "ModalOverlayHost",
            "CompactRoot", "CompactCaptionColumn", "CompactDragRegion", "CompactBody",
            // UI.8: the title strip's measured layout (CompactTitleLayout) and the Compact body host.
            "CompactBrandMark", "CompactWordmark", "CompactExpandButton", "CompactExpandText", "CompactShellView"
        ],
        // UI.8: the window focuses the first row / the state's real action when Compact opens (RC-8).
        ["Controls/CompactShell.xaml"] = ["CompactRepeater", "AddServerButton", "ManageHiddenButton"],
        // UI.4: adaptive VisualState setters reflow these; OverviewSearchBox also takes focus after "Limpar pesquisa";
        // Beacon r1: the code-behind focuses the content / the return target (HealthCard … EmptyAddButton).
        ["Views/DashboardPage.xaml"] = ["FirstServerStateBlock", "HiddenStateBlock", "PageViewport", "PageRoot", "HeaderGrid", "HeaderActions", "HealthRow", "HealthColumn2", "HealthCountRow", "HealthSegments",
            "PriorityHost", "ServersToolbar", "OverviewSearchBox", "StateOverlay",
            "HealthCard", "PriorityButton", "DirectoryLinkButton", "OverviewRepeater", "EmptyAddButton",
            // UI.7C (Cortex m-3): the editor's return focus finds a suggestion's "Adicionar" in these lists.
            "DiscoveredRepeater", "EmptyDiscoveredRepeater"],
        // UI.5: BackgroundSection is brought into view from code-behind; the rest are reflowed by the WidthStates setters.
        ["Views/SettingsPage.xaml"] = ["PageViewport", "BackgroundSection", "PageRoot", "AutosaveText", "ThemeControl", "LanguageControl", "BackgroundControl",
            "NotificationsControl", "CompactControl", "TopmostControl", "DisclosureColumn2", "DisclosureGrid", "AboutDisclosure",
            // UI.5 fix round 2 (Prism C1 M-8): the Narrow state spaces these rows.
            "ThemeRow", "CompactRow"],
        // UI.5 Server Detail: WidthStates setters + code-behind focus (HistoryRow / WorkloadsRow / RefreshButton).
        ["Views/ServerDetailPage.xaml"] = ["PageViewport", "PageScroll", "PageRoot", "IdentityGrid", "IdentityActions", "ServerNameHeading", "RefreshButton",
            "CollectingColumns", "MetricsGrid", "MetricsColumn2", "MetricsColumn3", "MemoryCard", "DiskCard", "DeeperGrid", "DeeperColumn2",
            "ExploreCard", "HistoryRow", "WorkloadsRow",
            // UI.5 fix round 2 (Beacon C1 N2): the live "A atualizar…" text announced from code-behind.
            "RefreshingText",
            // UI.5 fix round 2: focus returns to "…" after a cancelled / failed Ocultar or Remover.
            "MoreActionsButton"],
        // UI.5 §4: the Data sub-page brings the backup status and (H-UI5-4) the About card into view from code-behind.
        ["Views/SettingsDataPage.xaml"] = ["PageViewport", "BackupStatusBar", "AboutSection", "PageRoot", "ResetIgnoredButton", "HistoryActions", "BackupActions",
            "GitHubButton", "SuccessToast",
            // UI.5 fix round 3 (Beacon C2 R2-M1): focus returns to the history button that opened its dialog.
            "ClearHistoryButton", "ResetHistoryButton"],
        // UI.3: adaptive VisualState setters reflow these (no code-behind dependency).
        ["Views/HistoryPage.xaml"] = ["PageViewport", "ControlsFirstColumn", "RangeItems", "RangeLast30Days", "UnavailableState", "EmptyState", "PageRoot", "ControlsRow", "ServerSelector", "RangeTrack", "CpuChart", "MemoryChart", "DiskChart", "RetentionText"],
        ["Views/WorkloadsPage.xaml"] = ["PageViewport", "PageHost", "PageScroll", "PageRoot", "QueryRow", "SearchBox", "FilterTrack", "ServicesCard", "CardsGrid", "CardsColumn2", "CardsRow1", "CardsRow2", "ContainersScroll", "ServicesScroll",
            "DockerNoResultsText", "DockerNotInstalledState", "DockerPermissionState", "DockerUnavailableState", "DockerErrorState", "DockerEmptyState",
            "ServicesNoResultsText", "ServicesUnsupportedState", "ServicesUnavailableState", "ServicesErrorState", "ServicesEmptyState",
            "UnavailableState", "NothingState", "NoResultsState"],
        // UI.4: "Limpar pesquisa" returns focus to the search box (code-behind FocusAfterAction).
        ["Views/ServersPage.xaml"] = ["PageViewport", "SearchBox", "PageRoot", "HeaderGrid", "AddButton", "TableHeader", "HeaderSystemColumn", "HeaderSystemText",
            "HeaderActionColumn", "ServersRepeater", "RootGrid", "EmptyAddButton", "ReturnNotice"],
        ["Views/BackupCreateDialog.xaml"] = ["PassphraseBox", "ConfirmationBox"],
        ["Views/RestoreOpenDialog.xaml"] = ["PassphraseBox"],
        ["Views/AddServerDialog.xaml"] = ["ServerForm"],
        ["Views/EditServerDialog.xaml"] = ["ServerForm"],
        ["Controls/ServerFormControl.xaml"] =
        [
            "NameField", "PrepHelpLink", "PrepHelpContent", "PrepKeygenText", "PrepCopyKeygenButton",
            "PrepCopyKeyText", "PrepCopyKeyButton", "PrepPlaceholderNote", "PassphraseField",
            "PasswordField", "JumpPassphraseField", "JumpPasswordField",
            // UI.7C (B-16): every field that shows its own error, and its input (focus on the first invalid one).
            "NameFormField", "HostFormField", "HostField", "UsernameFormField", "UsernameField", "PortFormField", "PortField",
            "JumpHostFormField", "JumpHostField", "JumpUsernameFormField", "JumpUsernameField", "JumpPortFormField", "JumpPortField",
            // UI.7B: the key pickers (B-12), the saved-secret labels (G-13), the route line (B-13) and the import status.
            "KeyPickerButton", "KeyPickerText", "KeyPickerMenu", "PrivateKeyField", "PassphraseFormField", "PasswordFormField",
            "JumpKeyPickerButton", "JumpKeyPickerText", "JumpKeyPickerMenu", "JumpPrivateKeyField", "JumpPassphraseFormField",
            "JumpPasswordFormField", "RouteLine", "ImportStatusText",
            // UI.7A: the mode copy set from code-behind and the B-19 stacking setters.
            "IdentitySubtitle", "AuthTitle", "AuthSubtitle", "FormRoot", "CardsGrid", "CardsColumn2", "AuthCard",
            // UI.7 final c1 (Prism F-10): the Wide state keeps both cards at 384.
            "IdentityCard",
            // UI.7 final c2 (Prism C2-2): the narrow options card (selectors without the 200 minimum; Compact rows).
            "OperatingSystemField", "OperatingSystemSelector", "RefreshIntervalSelector", "OptionsColumn1", "OptionsColumn2",
            "JumpCardsGrid", "JumpCardsColumn2", "JumpAuthCard", "OptionsGrid", "OptionsColumn3", "RefreshIntervalField"
        ],
        // UI.7A Add / Edit server page: code-behind (header, actions, notice) and the WidthStates setters.
        ["Views/ServerEditorPage.xaml"] =
        [
            "PageViewport", "PageRoot", "HeaderTitle", "EditorHeading", "EditorSubtitle", "ServerForm", "SaveFailedNotice",
            "ActionBar", "TestButton", "ActionHint", "ActionEnd", "PrimaryButton",
            "CredentialNoteTitle", "HeaderButton",
            // UI.7B: the in-page modal layer (Cortex §8) and its two panels.
            "PageScroll", "DialogLayer", "DialogSurface", "TrustPanel", "ImportPanel",
            // UI.7C: the test panel in the same layer, the duplicate notice (H-UI7-2) and "Tentar guardar".
            "TestPanel", "DuplicateNotice", "OpenDuplicateButton", "RetrySaveButton",
            // UI.7A fix c1 (M-1): Cancelar is disabled while a Save is persisted.
            "CancelButton",
            // UI.7 final c1 (Prism F-1): the layer's maximum width follows the page.
            "EditorHost"
        ],
        // UI.7B panels of the editor page's in-page modal layer: the trust prompt (Figma 09; 7C adds the test) and "Importar de SSH".
        ["Views/ServerEditorTrustPanel.xaml"] =
        [
            "StepText", "TitleIcon", "TitleText", "BodyText", "SubjectText", "TrustedBlock", "TrustedLabel", "TrustedFingerprintText",
            "PresentedLabel", "PresentedFingerprintText", "ScopeText", "WorkingRow", "WorkingRing", "WorkingText", "CloseButton", "AcceptButton",
            "ActionRow"
        ],
        // UI.7C (B-9): the connection test in the same layer (Figma 08, the four real stages).
        ["Views/ServerEditorTestPanel.xaml"] =
        [
            "TitleIcon", "TitleText", "BodyText", "SubjectText", "StageList", "VerifiedDetailText", "CloseButton", "RetryButton",
            // UI.7 final c2 (Prism C2-1): the command row that stacks in a narrow dialog.
            "ActionRow"
        ],
        ["Views/SshConfigImportPanel.xaml"] =
        [
            "TitleText", "LoadingRow", "LoadingRing", "StatePanel", "StateIcon", "StateTitle", "StateBody", "ListPanel", "CountText", "HostList",
            "WarningText", "CancelButton", "UseButton",
            // UI.7C (H-UI7-2): the "Já adicionado" marker of a row (found by name in the recycled container).
            "AlreadyAddedText",
            // UI.7 final c1 (Prism F-2): a row's compact detail and jump lines, and the selected profile's details.
            "DetailText", "JumpText", "DetailsPanel", "DetailsHostLabel", "DetailsHostValue", "DetailsUserLabel", "DetailsUserValue",
            "DetailsKeyLabel", "DetailsKeyValue",
            // UI.7 final c2 (Prism C2-1 / C2-4): the stacking command row and the per-row state words.
            "ActionRow", "AvailableText", "SelectedText"
        ],
        // HistoryChart parts: the chart draws into these by name.
        ["Controls/HistoryChart.xaml"] = ["RootGrid", "PlotHost", "GridCanvas", "PlotCanvas", "YAxisCanvas", "XAxisCanvas"],
        // UI.2 primitive templates: names resolved at runtime by VisualState setters (G-3).
        ["Styles/Components/Sa.Primitives.xaml"] =
        [
            "PART_Dot", "PART_Label", "PART_Path", "PART_Header", "PART_Helper", "PART_Error", "PART_PasswordBox", "PART_RevealButton",
            // S6
            "RootGrid", "KeyColumn", "PART_Key", "PART_Value", "PART_Fill", "PART_Track", "PART_Text", "PART_Icon", "PART_Chevron",
            "PART_Detail", "PART_Trailing", "PART_InfoLayout", "PART_ErrorLayout", "PART_ErrorTitle", "PART_CloseButton", "PART_Parent",
            // UI.5 SaSegmentMeter / SaPulseBars: the host the code fills and the theme-aware brush sources it binds to.
            "PART_Host", "PART_LitBrush", "PART_EmptyBrush", "PART_BarBrush",
            // UI.8 SaCompactRowButtonStyle: the hover / pressed veil targeted by its VisualState setters.
            "StateOverlay"
        ],
        // UI.2 S4 control templates: names targeted by VisualState setters / storyboards (G-3).
        // UI.7 final c2 (Prism C2-4): the inset list row template (selected / hover overlays over the tint).
        ["Styles/Components/Sa.Surfaces.xaml"] = ["RowRoot", "SelectedOverlay", "HoverOverlay", "ContentPresenter"],
        ["Styles/Components/Sa.Buttons.xaml"] = ["RootGrid", "StateOverlay", "ContentPresenter", "Underline", "FocusUnderline"],
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
        "SystemColorButtonTextColor",
        // UI.7 final c1 (Prism F-4): HC inline text links (SaLinkTextBrush) use the framework hyperlink colour.
        "SystemColorHotlightColor"
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
    /// UI.7C (B-22, Vigil 7B N-1, Cortex n-7): the server editor is a PAGE; the legacy modal (whose submit path had no
    /// TrustAsync-window guard), the unused DialogReturnFocus and their resw keys are gone, so nobody can wire them back.
    /// The editor's old M14 ServerForm* / AddServerDialog* / EditServerDialog* / inline HostKey* keys are gone from every
    /// culture too (zero use proven by grep in the delivery).
    /// </summary>
    [Fact]
    public void TheLegacyServerEditorModal_AndItsKeys_AreGone()
    {
        Assert.False(File.Exists(AppSourceTree.Full("Controls/ServerEditorModal.xaml")));
        Assert.False(File.Exists(AppSourceTree.Full("Controls/ServerEditorModal.xaml.cs")));
        Assert.False(File.Exists(AppSourceTree.Full("Services/DialogReturnFocus.cs")));
        foreach (var culture in new[] { "pt-PT", "pt-BR", "en-US" })
        {
            var names = XDocument.Load(AppSourceTree.Full($"Resources/{culture}/Resources.resw"))
                .Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToList();
            Assert.DoesNotContain(names, name => name.StartsWith("AddServerDialog.", StringComparison.Ordinal)
                || name.StartsWith("EditServerDialog.", StringComparison.Ordinal)
                || name.StartsWith("ServerFormValidationError.", StringComparison.Ordinal)
                || name.StartsWith("ServerFormLocalKeySelector.", StringComparison.Ordinal)
                || name.StartsWith("ServerFormJumpLocalKeySelector.", StringComparison.Ordinal)
                || name.StartsWith("ServerFormSshConfigTitle.", StringComparison.Ordinal)
                || name.StartsWith("ServerFormSshConfigUseButton.", StringComparison.Ordinal)
                || name.StartsWith("ServerFormChecklistTitle.", StringComparison.Ordinal)
                || name.StartsWith("HostKeyTrustAndConnectButton.", StringComparison.Ordinal));
        }
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
