using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.2 S0 guards for the component layer (.boss/tmp/ui2/cortex-architecture.md §3/§5). They are written BEFORE
/// the component dictionaries and primitives exist, so each one is either vacuous on today's tree (and becomes
/// live the moment the folder it guards gains a file) or pins a property of the UI.1 token layer that UI.2 must
/// not disturb. Static only: XDocument over the checked-in sources, never bin/ or obj/.
/// </summary>
public sealed partial class ComponentLayerGuardTests
{
    private const string TokensFolder = "Styles/Tokens/";
    private const string ComponentsFolder = "Styles/Components/";
    private const string PrimitivesFolder = "Controls/Primitives/";
    private const string GalleryFolder = "Qa/Gallery/";
    private const string AccentScopeFile = "Styles/Components/Sa.AccentNeutralScope.xaml";
    private const string ComponentsAggregator = "Styles/Components/Sa.Components.xaml";
    private const string PrimitivesNamespace = "using:ServerMonitor.App.Controls.Primitives";

    // ---- T-2: StaticResource scope ---------------------------------------------------------------------

    /// <summary>
    /// T-2. Every <c>{StaticResource X}</c> in a token or component dictionary resolves inside that file's own
    /// lookup scope: a key defined lexically earlier in the same file, or a key of a dictionary it merges itself
    /// (recursively). It never depends on a sibling merged earlier by App.xaml - the documentation does not
    /// guarantee StaticResource lookup across sibling merged dictionaries during parse. ThemeResource is out of
    /// scope (resolved by the tree at runtime) and documented platform keys are allowed.
    /// </summary>
    [Fact]
    public void TokenAndComponentStaticResourcesResolveWithinTheirOwnScope()
    {
        var files = AppSourceTree.Files(".xaml").Where(f => IsUnder(f, TokensFolder) || IsUnder(f, ComponentsFolder)).ToList();
        Assert.NotEmpty(files);

        var failures = new List<string>();
        foreach (var file in files)
        {
            var document = AppSourceTree.LoadXaml(file);
            var merged = MergedKeysRecursive(file, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var keyed = document.Descendants().Where(e => e.Attribute(AppSourceTree.Xaml + "Key") is not null).ToList();

            foreach (var element in document.Descendants())
            {
                foreach (var key in StaticReferences(element))
                {
                    // Cortex F-7: an earlier key counts only when it lives in the consumer's own dictionary or one
                    // of its ancestors - never in a sibling theme dictionary or another element's resources.
                    var lexicallyBefore = keyed.Any(k => k != element
                        && (string)k.Attribute(AppSourceTree.Xaml + "Key")! == key
                        && k.IsBefore(element)
                        && !element.Ancestors().Contains(k)
                        && k.Parent is { } container && element.Ancestors().Contains(container));
                    if (!lexicallyBefore && !merged.Contains(key) && !XamlContractAndResourceGuardTests.IsPlatformKey(key))
                    {
                        failures.Add($"{file}: {{StaticResource {key}}} does not resolve in the file's own scope " +
                            "(define it earlier in the file or merge its token dictionary inside this file)");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // ---- T-5: component keys --------------------------------------------------------------------------

    /// <summary>
    /// T-5. A component dictionary only ADDS keys: each starts with <c>Sa</c> and none collides with a key that
    /// App.xaml, the legacy Styles/*.xaml, the token layer or another component dictionary already defines. The
    /// accent-neutral scope (F-3) is the one exception - overriding WinUI lightweight-styling keys is its job -
    /// and it is never merged app-wide (T-7).
    /// </summary>
    [Fact]
    public void ComponentDictionariesDefineOnlyNewSaKeys()
    {
        var existing = AppSourceTree.Files(".xaml")
            .Where(f => f == "App.xaml" || (AppSourceTree.IsUnderStyles(f) && !IsUnder(f, ComponentsFolder)))
            .SelectMany(f => DefinedKeys(AppSourceTree.LoadXaml(f)))
            .ToHashSet(StringComparer.Ordinal);

        var failures = new List<string>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in ComponentFiles().Where(f => f != AccentScopeFile))
        {
            foreach (var key in TopLevelAndThemeKeys(AppSourceTree.LoadXaml(file)))
            {
                if (!key.StartsWith("Sa", StringComparison.Ordinal) || key.Length < 3 || !char.IsUpper(key[2]))
                {
                    failures.Add($"{file}: key '{key}' must start with Sa (component keys are new, never overrides)");
                }

                if (existing.Contains(key) || XamlContractAndResourceGuardTests.IsPlatformKey(key))
                {
                    failures.Add($"{file}: key '{key}' collides with an existing app/token/platform key");
                }

                if (owners.TryGetValue(key, out var owner) && owner != file)
                {
                    failures.Add($"{file}: key '{key}' is also defined by {owner}");
                }

                owners.TryAdd(key, file);
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // ---- T-6: implicit styles -------------------------------------------------------------------------

    /// <summary>
    /// T-6. No implicit (unkeyed) style anywhere in the app, except the default style of a NEW <c>Sa*</c> type
    /// from the primitives namespace - by construction that cannot reach a production element. An implicit
    /// <c>Style TargetType="Button"</c> would restyle every button in the product.
    /// </summary>
    [Fact]
    public void NoImplicitStylesExceptSaPrimitiveDefaults()
    {
        var offenders = new List<string>();
        foreach (var file in AppSourceTree.Files(".xaml"))
        {
            foreach (var style in AppSourceTree.LoadXaml(file).Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                if (style.Attribute(AppSourceTree.Xaml + "Key") is not null || IsInsideSetterValue(style))
                {
                    continue;
                }

                var targetType = (string?)style.Attribute("TargetType") ?? string.Empty;
                if (!IsSaPrimitiveType(style, targetType))
                {
                    offenders.Add($"{file}: implicit <Style TargetType=\"{targetType}\"> (give it an x:Key; only primitives:Sa* default styles may be implicit)");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    // ---- T-7: merge order -----------------------------------------------------------------------------

    /// <summary>
    /// T-7. Components are merged by App.xaml exactly once and LAST (after the legacy Controls.xaml), so they can
    /// only add keys (T-5 proves they add nothing that collides). The F-3 accent-neutral scope is never merged
    /// app-wide - neither by App.xaml nor through the aggregator - because that would change every production
    /// CheckBox/ToggleSwitch/ProgressBar at once.
    /// </summary>
    [Fact]
    public void AppXamlMergesComponentsLastAndScopeNever()
    {
        var appSources = MergedSources(AppSourceTree.LoadXaml("App.xaml")).ToList();
        Assert.NotEmpty(appSources);

        if (ComponentFiles().Count > 0)
        {
            Assert.True(File.Exists(AppSourceTree.Full(ComponentsAggregator)),
                $"Styles/Components/** has files but no aggregator {ComponentsAggregator}");
            Assert.Equal(ComponentsAggregator, NormalizeSource("App.xaml", appSources[^1]));
            Assert.Single(appSources, s => NormalizeSource("App.xaml", s) == ComponentsAggregator);
        }
        else
        {
            Assert.DoesNotContain(appSources, s => NormalizeSource("App.xaml", s).StartsWith(ComponentsFolder, StringComparison.OrdinalIgnoreCase));
        }

        var mergers = new[] { "App.xaml" }.Concat(ComponentFiles());
        foreach (var file in mergers.Where(f => File.Exists(AppSourceTree.Full(f))))
        {
            Assert.DoesNotContain(MergedSources(AppSourceTree.LoadXaml(file)), s => NormalizeSource(file, s) == AccentScopeFile);
        }
    }

    // ---- T-10: Color.Primitives -----------------------------------------------------------------------

    /// <summary>
    /// T-10. Color.Primitives is merged twice (by Color.Semantic and by Elevation) and that is harmless ONLY
    /// because it holds value-type <c>Color</c> entries with no shared identity. The day it gains a reference
    /// object (a brush, a style) the duplicate stops being harmless and this fails, forcing a consolidation.
    /// </summary>
    [Fact]
    public void ColorPrimitivesIsValueOnlyAndMergedOnlyByItsTwoOwners()
    {
        const string primitives = "Styles/Tokens/Color.Primitives.xaml";
        var root = AppSourceTree.LoadXaml(primitives).Root!;
        var nonColour = root.Elements().Where(e => e.Name.LocalName != "Color").Select(e => e.Name.LocalName).ToList();
        Assert.True(nonColour.Count == 0, $"{primitives} must hold only <Color> entries; found {string.Join(", ", nonColour)}");
        Assert.NotEmpty(root.Elements());

        var mergers = AppSourceTree.Files(".xaml")
            .Where(file => MergedSources(AppSourceTree.LoadXaml(file)).Any(s => NormalizeSource(file, s) == primitives))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["Styles/Tokens/Color.Semantic.xaml", "Styles/Tokens/Elevation.xaml"], mergers);
    }

    // ---- T-11: primitives and gallery stay presentation-only ------------------------------------------

    /// <summary>
    /// T-11. Primitives and the Debug gallery know nothing of view-models, the composition root or the domain:
    /// they take primitive/enum dependency properties only. A <c>UserControl</c> is forbidden in Primitives (its
    /// definition scope has its own resource lookup and its code-behind invites logic).
    /// </summary>
    [Fact]
    public void PrimitivesHaveNoViewModelOrServiceKnowledge()
    {
        var offenders = new List<string>();
        foreach (var file in AppSourceTree.Files(".cs").Where(f => IsUnder(f, PrimitivesFolder) || IsUnder(f, GalleryFolder)))
        {
            var code = AppSourceTree.CodeWithoutComments(file);
            var matches = ForbiddenPresentationDependency().Matches(code).Select(m => m.Value).ToList();
            // Vigil F-2: the --qa-tokens self-check reads App.ServicesHost exactly once, to assert the gallery never built
            // the host. That is the opposite of a dependency; any other mention still fails.
            if (file == "Qa/Gallery/QaTokenSelfCheck.cs" && matches.Count(m => m == "ServicesHost") == 1
                && code.Contains("App.ServicesHost is not null", StringComparison.Ordinal))
            {
                matches.Remove("ServicesHost");
            }

            offenders.AddRange(matches.Select(m => $"{file}: {m}"));
            if (IsUnder(file, PrimitivesFolder) && UserControlUse().IsMatch(code))
            {
                offenders.Add($"{file}: UserControl is forbidden in Controls/Primitives/** (use a templated Control)");
            }
        }

        foreach (var file in AppSourceTree.Files(".xaml").Where(f => IsUnder(f, PrimitivesFolder)))
        {
            offenders.Add($"{file}: Controls/Primitives/** holds templated controls only; their default style lives in Styles/Components/Sa.Primitives.xaml");
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    // ---- T-12: legacy Default dictionary --------------------------------------------------------------

    /// <summary>
    /// T-12. DesignTokens.xaml carries a <c>Default</c> theme dictionary that is never selected (explicit Dark,
    /// Light and HighContrast exist). It is left untouched in UI.2; this proves, continuously, that it is a
    /// byte-for-byte equivalent of <c>Dark</c>, which is what makes removing it later (UI.6) zero-drift.
    /// </summary>
    [Fact]
    public void LegacyDefaultThemeDictionaryEqualsDark()
    {
        var themes = AppSourceTree.LoadXaml("Styles/DesignTokens.xaml").Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries")
            .SelectMany(block => block.Elements())
            .ToDictionary(e => (string)e.Attribute(AppSourceTree.Xaml + "Key")!, StringComparer.Ordinal);

        var dark = Entries(themes["Dark"]);
        var legacyDefault = Entries(themes["Default"]);
        Assert.NotEmpty(dark);

        var failures = dark.Keys.Union(legacyDefault.Keys).OrderBy(k => k, StringComparer.Ordinal)
            .Where(key => !dark.TryGetValue(key, out var d) || !legacyDefault.TryGetValue(key, out var l) || d != l)
            .Select(key => $"{key}: Dark={dark.GetValueOrDefault(key) ?? "<missing>"} Default={legacyDefault.GetValueOrDefault(key) ?? "<missing>"}")
            .ToList();
        Assert.True(failures.Count == 0, "DesignTokens Default must equal Dark:" + Environment.NewLine + string.Join(Environment.NewLine, failures));

        static Dictionary<string, string> Entries(XElement dictionary) => dictionary.Elements().ToDictionary(
            e => (string)e.Attribute(AppSourceTree.Xaml + "Key")!,
            e => new XElement(e.Name, e.Attributes().Where(a => a.Name != AppSourceTree.Xaml + "Key"), e.Nodes())
                .ToString(SaveOptions.DisableFormatting),
            StringComparer.Ordinal);
    }

    // ---- T-19 (G-3): template parts and visual states -------------------------------------------------

    /// <summary>
    /// T-19 (G-3). Every <c>[TemplatePart]</c> / <c>GetTemplateChild("X")</c> of a primitive exists as an
    /// <c>x:Name</c> in the template of its default style, and every <c>[TemplateVisualState]</c> it declares exists
    /// as a VisualState in the matching group. A rename on either side fails here instead of silently leaving a
    /// part null or a GoToState that does nothing.
    /// </summary>
    [Fact]
    public void TemplatePartsExistInDefaultTemplates()
    {
        var failures = new List<string>();
        var defaults = DefaultStyles();
        foreach (var file in AppSourceTree.Files(".cs").Where(f => IsUnder(f, PrimitivesFolder)))
        {
            var code = AppSourceTree.CodeWithoutComments(file);
            foreach (Match type in TemplatedClass().Matches(code))
            {
                var typeName = type.Groups[1].Value;
                var parts = XamlContractAndResourceGuardTests.RuntimeNameLookups(code).ToHashSet(StringComparer.Ordinal);
                var states = TemplateVisualStateAttribute().Matches(code)
                    .Select(m => (State: m.Groups["name"].Value, Group: m.Groups["group"].Value))
                    .ToList();
                if (parts.Count == 0 && states.Count == 0)
                {
                    continue;
                }

                if (!defaults.TryGetValue(typeName, out var style))
                {
                    failures.Add($"{file}: {typeName} declares template parts/states but has no default style in Styles/Components/**");
                    continue;
                }

                var names = style.Descendants().Attributes(AppSourceTree.Xaml + "Name").Select(a => a.Value).ToHashSet(StringComparer.Ordinal);
                failures.AddRange(parts.Where(p => !names.Contains(p)).Select(p => $"{typeName}: template part '{p}' has no x:Name in its default template"));

                foreach (var (state, group) in states)
                {
                    var groupElement = style.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualStateGroup"
                        && (string?)e.Attribute(AppSourceTree.Xaml + "Name") == group);
                    if (groupElement is null || !groupElement.Descendants().Any(e => e.Name.LocalName == "VisualState"
                            && (string?)e.Attribute(AppSourceTree.Xaml + "Name") == state))
                    {
                        failures.Add($"{typeName}: visual state '{group}.{state}' is not in its default template");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // ---- T-21: legacy alias map ----------------------------------------------------------------------

    /// <summary>
    /// T-21. docs/ui/ui1-tokens.md §9 maps all 70 legacy DesignTokens.xaml keys to their future Sa* destinations.
    /// UI.2 removes no alias: the map and the dictionary keep naming exactly the same 70 keys.
    /// </summary>
    [Fact]
    public void AliasMapMatchesDesignTokens()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppSourceTree.AppRoot, "..", ".."));
        var doc = File.ReadAllText(Path.Combine(repositoryRoot, "docs", "ui", "ui1-tokens.md"));
        var section = AliasSection().Match(doc);
        Assert.True(section.Success, "docs/ui/ui1-tokens.md lost its '## 9.' alias map section");

        var mapped = AliasRow().Matches(section.Value).Select(m => m.Groups[1].Value)
            .Where(k => k != "Chave antiga").ToList();
        Assert.Equal(70, mapped.Count);
        Assert.Equal(mapped.Count, mapped.Distinct(StringComparer.Ordinal).Count());

        string[] themeKeys = ["Dark", "Light", "Default", "HighContrast"];
        var defined = DefinedKeys(AppSourceTree.LoadXaml("Styles/DesignTokens.xaml"))
            .Where(k => !themeKeys.Contains(k)).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(mapped.Where(k => !defined.Contains(k)).Select(k => $"mapped but no longer in DesignTokens.xaml: {k}"));
        Assert.Empty(defined.Where(k => !mapped.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).Select(k => $"in DesignTokens.xaml but not in the §9 map: {k}"));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static bool IsUnder(string relative, string folder) => relative.StartsWith(folder, StringComparison.OrdinalIgnoreCase);

    private static List<string> ComponentFiles() =>
        AppSourceTree.Files(".xaml").Where(f => IsUnder(f, ComponentsFolder)).ToList();

    private static HashSet<string> DefinedKeys(XDocument document) =>
        document.Descendants().Attributes(AppSourceTree.Xaml + "Key").Select(a => a.Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>Keys the dictionary contributes to lookup: its own entries and its theme-dictionary entries
    /// (keys local to a template's or a style's own Resources are not dictionary keys).</summary>
    private static IEnumerable<string> TopLevelAndThemeKeys(XDocument document)
    {
        var root = document.Root!;
        foreach (var entry in root.Elements().Where(e => !e.Name.LocalName.StartsWith("ResourceDictionary.", StringComparison.Ordinal)))
        {
            if (entry.Attribute(AppSourceTree.Xaml + "Key") is { } key)
            {
                yield return key.Value;
            }
        }

        foreach (var theme in root.Elements().Where(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries").Elements())
        {
            foreach (var entry in theme.Elements())
            {
                if (entry.Attribute(AppSourceTree.Xaml + "Key") is { } key)
                {
                    yield return key.Value;
                }
            }
        }
    }

    private static IEnumerable<string> StaticReferences(XElement element)
    {
        foreach (var attribute in element.Attributes())
        {
            foreach (Match match in StaticMarkup().Matches(attribute.Value))
            {
                yield return match.Groups[1].Value;
            }
        }

        if (element.Name.LocalName == "StaticResource" && element.Attribute("ResourceKey") is { } key)
        {
            yield return key.Value;
        }
    }

    private static IEnumerable<string> MergedSources(XDocument document) =>
        document.Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary.MergedDictionaries")
            .Elements()
            .Select(e => (string?)e.Attribute("Source"))
            .OfType<string>();

    /// <summary>App-relative path of a merged Source: <c>ms-appx:///X</c> is app-rooted, anything else is relative
    /// to the merging file's folder.</summary>
    private static string NormalizeSource(string mergingFile, string source)
    {
        const string appx = "ms-appx:///";
        if (source.StartsWith(appx, StringComparison.OrdinalIgnoreCase))
        {
            return source[appx.Length..].Replace('\\', '/');
        }

        var folder = Path.GetDirectoryName(mergingFile.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var combined = Path.GetRelativePath(AppSourceTree.AppRoot, Path.GetFullPath(Path.Combine(AppSourceTree.AppRoot, folder, source)));
        return combined.Replace('\\', '/');
    }

    private static HashSet<string> MergedKeysRecursive(string file, HashSet<string> visited)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in MergedSources(AppSourceTree.LoadXaml(file)).Select(s => NormalizeSource(file, s)))
        {
            if (!visited.Add(source))
            {
                continue;
            }

            Assert.True(File.Exists(AppSourceTree.Full(source)), $"{file} merges {source}, which does not exist");
            keys.UnionWith(DefinedKeys(AppSourceTree.LoadXaml(source)));
            keys.UnionWith(MergedKeysRecursive(source, visited));
        }

        return keys;
    }

    private static bool IsInsideSetterValue(XElement style) =>
        style.Ancestors().Any(a => a.Name.LocalName is "Setter.Value");

    private static bool IsSaPrimitiveType(XElement style, string targetType)
    {
        var colon = targetType.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        var prefix = targetType[..colon];
        var typeName = targetType[(colon + 1)..];
        var ns = style.GetNamespaceOfPrefix(prefix);
        return ns is not null
            && string.Equals(ns.NamespaceName, PrimitivesNamespace, StringComparison.Ordinal)
            && typeName.StartsWith("Sa", StringComparison.Ordinal);
    }

    /// <summary>
    /// Implicit default styles of primitives in Styles/Components/**, by type name (e.g. SaStatusIndicator). UI.3: an
    /// implicit style that is only <c>BasedOn</c> a keyed base (so keyed variants can share the template) resolves to
    /// that base, so the template it actually gets is the one checked - never an empty implicit shell.
    /// </summary>
    private static Dictionary<string, XElement> DefaultStyles()
    {
        var styles = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var keyed = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var implicitStyles = new List<(string TypeName, XElement Style)>();
        foreach (var file in ComponentFiles())
        {
            foreach (var style in AppSourceTree.LoadXaml(file).Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                var key = (string?)style.Attribute(AppSourceTree.Xaml + "Key");
                if (key is not null)
                {
                    keyed.TryAdd(key, style);
                    continue;
                }

                var targetType = (string?)style.Attribute("TargetType") ?? string.Empty;
                if (IsSaPrimitiveType(style, targetType))
                {
                    implicitStyles.Add((targetType[(targetType.IndexOf(':') + 1)..], style));
                }
            }
        }

        foreach (var (typeName, style) in implicitStyles)
        {
            var resolved = style;
            for (var depth = 0; depth < 4 && !resolved.Descendants().Any(e => e.Name.LocalName == "ControlTemplate"); depth++)
            {
                var basedOn = StaticMarkup().Match((string?)resolved.Attribute("BasedOn") ?? string.Empty);
                if (!basedOn.Success || !keyed.TryGetValue(basedOn.Groups[1].Value, out var baseStyle))
                {
                    break;
                }

                resolved = baseStyle;
            }

            styles[typeName] = resolved;
        }

        return styles;
    }

    [GeneratedRegex(@"\{StaticResource\s+(?:ResourceKey\s*=\s*)?([^\s,}]+)")]
    private static partial Regex StaticMarkup();

    [GeneratedRegex(@"\bServicesHost\b|\bViewModels\b|\bServerMonitor\.(?:Core|Infrastructure|Collectors|Features)\b|(?<![\w.])(?:Core|Infrastructure|Collectors|Features)\.[A-Z]")]
    private static partial Regex ForbiddenPresentationDependency();

    [GeneratedRegex(@"\bUserControl\b")]
    private static partial Regex UserControlUse();

    [GeneratedRegex(@"\bclass\s+(Sa\w+)\s*:")]
    private static partial Regex TemplatedClass();

    [GeneratedRegex(@"\[\s*TemplateVisualState\s*\(\s*(?:Name\s*=\s*""(?<name>[^""]+)""\s*,\s*GroupName\s*=\s*""(?<group>[^""]+)""|GroupName\s*=\s*""(?<group>[^""]+)""\s*,\s*Name\s*=\s*""(?<name>[^""]+)"")")]
    private static partial Regex TemplateVisualStateAttribute();

    [GeneratedRegex(@"^## 9\..*?(?=^## 10\.)", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex AliasSection();

    [GeneratedRegex(@"^\|\s*([A-Za-z][A-Za-z0-9]*)\s*\|", RegexOptions.Multiline)]
    private static partial Regex AliasRow();
}
