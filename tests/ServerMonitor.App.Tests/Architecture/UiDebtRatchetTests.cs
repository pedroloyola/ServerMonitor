using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.1 debt ratchets (docs/ui/ui0-figma-audit.md §0/§6): the presentation rebuild may only REDUCE these
/// counts. Each fails on growth and passes on today's tree; none fails when debt is removed.
/// Scope (UI.1 review G-2): every app XAML/C# source, INCLUDING the legacy dictionaries Styles/DesignTokens.xaml
/// and Styles/Controls.xaml (baselined like any page). Only the new token layer, Styles/Tokens/** (scanned
/// generically), is exempt - and it must never reference the legacy accent (asserted separately).
///
/// HOW TO LOWER A BASELINE: in the same PR that removes the debt, lower (or delete) the file's entry below to
/// the new count reported by the test. Never raise a baseline; move the value into a Styles/** token instead.
/// </summary>
public sealed partial class UiDebtRatchetTests
{
    /// <summary>Literal <c>FontSize="n"</c> (attribute or <c>Setter Property="FontSize" Value="n"</c>) per file,
    /// for every XAML except App.xaml and Styles/Tokens/** (Views/, Controls/, MainWindow.xaml, legacy Styles/*.xaml,
    /// any new folder). Files not listed have a baseline of zero. Total today: 96 (UI.3 removed HistoryPage 7 + WorkloadsPage 38; UI.4 DashboardPage 8; UI.5 ServerFullCard 21 + ServerActionsButton 1 + SettingsPage 20).</summary>
    private static readonly IReadOnlyDictionary<string, int> FontSizeLiteralBaseline = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Controls/DiscoveredServerCard.xaml"] = 5,
        ["Controls/EmptyStateControl.xaml"] = 3,
        ["Controls/ServerCompactCard.xaml"] = 15,
        ["Controls/ServerEditorModal.xaml"] = 1,
        ["Controls/ServerFormControl.xaml"] = 43, // UI.7A: the cards/fields moved to Sa type-ramp styles; the temporary inline panels remain
        ["MainWindow.xaml"] = 7,
        ["Styles/Controls.xaml"] = 7,
        ["Views/BackupCreateDialog.xaml"] = 3,
        ["Views/RestoreConfirmDialog.xaml"] = 3,
        ["Views/RestoreOpenDialog.xaml"] = 2
        // UI.5 B2: SettingsPage / SettingsDataPage were rebuilt on the type ramp - their 20 literals are gone (win locked).
    };

    /// <summary>Hex colour literals per file, outside Styles/Tokens/** (XAML values and C# string literals; comments
    /// excluded). Files not listed have a baseline of zero.</summary>
    private static readonly IReadOnlyDictionary<string, int> HexColourBaseline = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Styles/DesignTokens.xaml"] = 165
    };

    /// <summary>Legacy blue (#1846E1-derived) dependencies per file, outside Styles/Tokens/** (UI.1 review G-1):
    /// <c>BrandAccent*</c>, <c>AccentSoft*</c>, <c>AccentText*</c>, <c>AccentPill*</c> (incl. AccentPillButtonStyle),
    /// <c>AccentFill*</c>, <c>SystemAccent*</c> and the <c>#1846E1</c> literal. Counted in XAML attribute values/text and
    /// C# code (comments excluded). Files not listed have a baseline of zero.</summary>
    private static readonly IReadOnlyDictionary<string, int> LegacyAccentBaseline = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Controls/EmptyStateControl.xaml"] = 3,
        ["Controls/ServerCompactCard.xaml"] = 3,
        ["Controls/ServerFormControl.xaml"] = 1,
        ["MainWindow.xaml"] = 4,
        ["Styles/Controls.xaml"] = 4,
        ["Styles/DesignTokens.xaml"] = 92
    };

    /// <summary>
    /// Cortex F-11: the WinUI lightweight-styling key NAMES that the F-3 accent-neutral scope overrides. They contain
    /// "AccentFill" but are not a use of the legacy blue: they are the keys being neutralised. They are exempt from the
    /// legacy-accent count ONLY as <c>x:Key</c> attribute values - by name, never by file, never a baseline - so a
    /// <c>#1846E1</c> / <c>SystemAccent*</c> VALUE anywhere, or any other accent key name, still counts.
    /// <see cref="AccentScopeOverridesExactlyTheExemptKeyNames"/> keeps this list identical to the scope's keys.
    /// </summary>
    internal static readonly IReadOnlySet<string> AccentScopeOverriddenKeyNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
        "AccentFillColorDisabledBrush",
        "AccentFillColorSelectedTextBackgroundBrush",
        "TextOnAccentFillColorPrimaryBrush"
    };

    [Fact]
    public void LiteralFontSizesOutsideTokens_DoNotGrow()
    {
        var counts = AppSourceTree.Files(".xaml")
            .Where(file => file != "App.xaml" && !IsUnderTokens(file))
            .ToDictionary(file => file, file => CountFontSizeLiterals(AppSourceTree.LoadXaml(file)), StringComparer.Ordinal);

        AssertNoGrowth(counts, FontSizeLiteralBaseline, "literal FontSize values (use a type-ramp style/token from Styles/**)");
    }

    [Fact]
    public void HexColourLiteralsOutsideTokens_DoNotGrow()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in AppSourceTree.Files(".xaml").Where(file => !IsUnderTokens(file)))
        {
            counts[file] = XamlValues(AppSourceTree.LoadXaml(file)).Sum(value => HexColour().Matches(value).Count);
        }

        foreach (var file in AppSourceTree.Files(".cs").Where(file => !IsUnderTokens(file)))
        {
            counts[file] = StringLiteral().Matches(AppSourceTree.CodeWithoutComments(file))
                .Sum(literal => HexColour().Matches(literal.Value).Count);
        }

        AssertNoGrowth(counts, HexColourBaseline, "hex colour literals (define a Styles/Tokens/** token and reference it)");
    }

    [Fact]
    public void LegacyAccentDependenciesOutsideTokens_DoNotGrow()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in AppSourceTree.Files(".xaml").Where(file => !IsUnderTokens(file)))
        {
            counts[file] = XamlValuesExceptExemptAccentKeys(AppSourceTree.LoadXaml(file)).Sum(value => LegacyAccent().Matches(value).Count);
        }

        foreach (var file in AppSourceTree.Files(".cs").Where(file => !IsUnderTokens(file)))
        {
            counts[file] = LegacyAccent().Matches(AppSourceTree.CodeWithoutComments(file)).Count;
        }

        AssertNoGrowth(counts, LegacyAccentBaseline,
            "legacy accent dependencies (BrandAccent*/AccentSoft*/AccentText*/AccentPill*/AccentFill*/SystemAccent*/#1846E1; use Sa* tokens)");
    }

    /// <summary>The token layer is exempt from the ratchets above, so it must never carry the legacy accent.</summary>
    [Fact]
    public void TokenDictionariesNeverReferenceTheLegacyAccent()
    {
        var offenders = AppSourceTree.Files(".xaml")
            .Where(IsUnderTokens)
            .SelectMany(file => XamlValues(AppSourceTree.LoadXaml(file))
                .SelectMany(value => ForbiddenInTokens().Matches(value))
                .Select(match => $"{file}: {match.Value}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Styles/Tokens/** must not reference #1846E1, SystemAccent* or BrandAccent*:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// UI.2 T-8: the new presentation layer starts at ZERO debt. Styles/Components/**, Controls/Primitives/** and
    /// the Debug gallery (Qa/**) are scanned by the three ratchets above like any other file, and none of them may
    /// ever be given a baseline entry - a literal there is a defect to fix, not debt to record.
    /// </summary>
    [Fact]
    public void NewComponentFoldersCarryNoRatchetBaseline()
    {
        string[] zeroDebtFolders = ["Styles/Components/", "Controls/Primitives/", "Qa/"];
        var baselined = FontSizeLiteralBaseline.Keys.Concat(HexColourBaseline.Keys).Concat(LegacyAccentBaseline.Keys)
            .Where(file => zeroDebtFolders.Any(folder => file.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(baselined.Count == 0, "Zero-debt folders must not be baselined: " + string.Join(", ", baselined));
    }

    /// <summary>
    /// UI.2 T-8 (F-3): component dictionaries and primitives never USE the legacy blue - no <c>#1846E1</c>,
    /// <c>SystemAccent*</c>, <c>BrandAccent*</c> or <c>AccentFill*</c> value or code reference. The only place such a
    /// name may appear is as an overridden <c>x:Key</c> in the accent-neutral scope, whose job is to replace it.
    /// </summary>
    [Fact]
    public void ComponentsNeverReferenceTheLegacyAccent()
    {
        var offenders = new List<string>();
        foreach (var file in AppSourceTree.Files(".xaml").Where(IsComponentLayer))
        {
            offenders.AddRange(XamlValuesExceptExemptAccentKeys(AppSourceTree.LoadXaml(file))
                .SelectMany(v => LegacyAccentInComponents().Matches(v)).Select(m => $"{file}: {m.Value}"));
        }

        foreach (var file in AppSourceTree.Files(".cs").Where(IsComponentLayer))
        {
            offenders.AddRange(LegacyAccentInComponents().Matches(AppSourceTree.CodeWithoutComments(file)).Select(m => $"{file}: {m.Value}"));
        }

        Assert.True(offenders.Count == 0,
            "The component layer must not use the legacy accent (#1846E1/SystemAccent*/BrandAccent*/AccentFill*):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));

        static bool IsComponentLayer(string file) =>
            file.StartsWith("Styles/Components/", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("Controls/Primitives/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// UI.2 token layering: raw colour tokens (<c>SaColor*</c>) are private to Styles/Tokens/**, which maps them
    /// to semantic brushes. Everything else consumes <c>Sa*Brush</c> through <c>{ThemeResource}</c> so Light/Dark/HC
    /// re-resolve at runtime; a <c>{StaticResource Sa...Brush}</c> outside Styles/** would freeze the theme.
    /// Zero today; stays zero.
    /// </summary>
    [Fact]
    public void SaColourTokensStayInsideTokensAndSaBrushesAreThemeResources()
    {
        var offenders = new List<string>();
        foreach (var file in AppSourceTree.Files(".xaml"))
        {
            var references = AppSourceTree.XamlResourceReferences(AppSourceTree.LoadXaml(file)).ToList();
            if (!file.StartsWith("Styles/Tokens/", StringComparison.OrdinalIgnoreCase))
            {
                offenders.AddRange(references
                    .Where(reference => reference.Key.StartsWith("SaColor", StringComparison.Ordinal))
                    .Select(reference => $"{file}: {{{reference.Kind} {reference.Key}}} - SaColor* is private to Styles/Tokens/**; use a Sa*Brush"));
            }

            if (!AppSourceTree.IsUnderStyles(file))
            {
                offenders.AddRange(references
                    .Where(reference => reference.Kind == "StaticResource" && SaBrushKey().IsMatch(reference.Key))
                    .Select(reference => $"{file}: {{StaticResource {reference.Key}}} - use {{ThemeResource {reference.Key}}}"));
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private static void AssertNoGrowth(
        IReadOnlyDictionary<string, int> actual,
        IReadOnlyDictionary<string, int> baseline,
        string what)
    {
        var grown = actual
            .Where(pair => pair.Value > baseline.GetValueOrDefault(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}: {pair.Value} > baseline {baseline.GetValueOrDefault(pair.Key)}")
            .ToList();

        Assert.True(grown.Count == 0,
            $"New {what} - the ratchet only goes down:{Environment.NewLine}{string.Join(Environment.NewLine, grown)}");
    }

    private static int CountFontSizeLiterals(XDocument document)
    {
        var attributes = document.Descendants().Attributes()
            .Count(a => a.Name.LocalName == "FontSize" && NumericLiteral().IsMatch(a.Value.Trim()));
        var setters = document.Descendants()
            .Where(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "FontSize")
            .Count(e => e.Attribute("Value") is { } value && NumericLiteral().IsMatch(value.Value.Trim()));
        return attributes + setters;
    }

    /// <summary>Vigil F-1: the legacy blue is caught with or without an alpha byte, in any case (RRGGBB / AARRGGBB only).</summary>
    [Theory]
    [InlineData("#1846E1", true)]
    [InlineData("#FF1846E1", true)]
    [InlineData("#ff1846e1", true)]
    [InlineData("Color=\"#FF1846E1\"", true)]
    [InlineData("#401846E1", true)]
    [InlineData("#1846E1FF", false)]
    [InlineData("#F5F5F5", false)]
    public void LegacyAccentHexIsCaughtInEveryValidForm(string value, bool expected)
    {
        Assert.Equal(expected, LegacyAccent().IsMatch(value));
        Assert.Equal(expected, ForbiddenInTokens().IsMatch(value));
        Assert.Equal(expected, LegacyAccentInComponents().IsMatch(value));
    }

    /// <summary>F-11: the exempt key names must be exactly the legacy-accent-shaped keys the F-3 scope overrides.</summary>
    [Fact]
    public void AccentScopeOverridesExactlyTheExemptKeyNames()
    {
        var scopeKeys = AppSourceTree.LoadXaml("Styles/Components/Sa.AccentNeutralScope.xaml").Descendants()
            .Attributes(AppSourceTree.Xaml + "Key").Select(a => a.Value)
            .Where(key => LegacyAccent().IsMatch(key))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(AccentScopeOverriddenKeyNames.Order(StringComparer.Ordinal), scopeKeys.Order(StringComparer.Ordinal));
    }

    private static IEnumerable<string> XamlValuesExceptExemptAccentKeys(XDocument document) =>
        document.Descendants().Attributes()
            .Where(a => !(a.Name == AppSourceTree.Xaml + "Key" && AccentScopeOverriddenKeyNames.Contains(a.Value)))
            .Select(a => a.Value)
            .Concat(document.DescendantNodes().OfType<XText>().Select(t => t.Value));

    /// <summary>Attribute values and element text; XML comments are never included.</summary>
    private static IEnumerable<string> XamlValues(XDocument document) =>
        document.Descendants().Attributes().Select(a => a.Value)
            .Concat(document.DescendantNodes().OfType<XText>().Select(t => t.Value));

    [GeneratedRegex(@"^\d+(\.\d+)?$")]
    private static partial Regex NumericLiteral();

    [GeneratedRegex(@"(?<![\w&])#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3,4})\b")]
    private static partial Regex HexColour();

    private static bool IsUnderTokens(string relative) =>
        relative.StartsWith("Styles/Tokens/", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"BrandAccent\w*|AccentSoft\w*|AccentText\w*|AccentPill\w*|AccentFill\w*|SystemAccent\w*|#(?:[0-9A-F]{2})?1846E1\b", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyAccent();

    [GeneratedRegex(@"#(?:[0-9A-F]{2})?1846E1\b|SystemAccent\w*|BrandAccent\w*", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenInTokens();

    [GeneratedRegex(@"#(?:[0-9A-F]{2})?1846E1\b|SystemAccent\w*|BrandAccent\w*|AccentFill\w*", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyAccentInComponents();

    [GeneratedRegex(@"^Sa[A-Z]\w*Brush$")]
    private static partial Regex SaBrushKey();

    [GeneratedRegex(@"""(?:\\.|[^""\\\n])*""")]
    private static partial Regex StringLiteral();
}
