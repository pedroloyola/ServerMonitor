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
        ["Controls/HistoryChart.xaml"] = ["RootGrid", "GridCanvas", "PlotCanvas"]
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
        "SystemColorWindowTextColor"
    };

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

    /// <summary>Keeps the contract honest: a new code-behind or ElementName dependency must be inventoried.</summary>
    [Fact]
    public void ContractInventoryCoversEveryNameCodeBehindOrElementNameDependsOn()
    {
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
        foreach (var file in AppSourceTree.Files(".xaml").Where(f => f.StartsWith("Styles/Tokens/", StringComparison.OrdinalIgnoreCase)))
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

    [GeneratedRegex(@"ElementName\s*=\s*([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex ElementNameReference();

    [GeneratedRegex(@"Resources\[""([^""]+)""\](?!\s*=[^=])")]
    private static partial Regex LiteralResourceRead();

    [GeneratedRegex(@"Resources\[[A-Za-z_][A-Za-z0-9_]*\]")]
    private static partial Regex VariableResourceRead();

    [GeneratedRegex(@"""([A-Z][A-Za-z0-9]*(?:Brush|Style))""")]
    private static partial Regex ResourceKeyShapedLiteral();
}
