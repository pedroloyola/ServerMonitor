using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.1 debt ratchets (docs/ui/ui0-figma-audit.md §0/§6): the presentation rebuild may only REDUCE these
/// counts. Each fails on growth and passes on today's tree; none fails when debt is removed.
/// "Outside Styles" means every app XAML/C# source except Styles/** (the token/style dictionaries, scanned
/// generically so new files under e.g. Styles/Tokens/ are exempt without editing this test).
///
/// HOW TO LOWER A BASELINE: in the same PR that removes the debt, lower (or delete) the file's entry below to
/// the new count reported by the test. Never raise a baseline; move the value into a Styles/** token instead.
/// </summary>
public sealed partial class UiDebtRatchetTests
{
    /// <summary>Literal <c>FontSize="n"</c> (attribute or <c>Setter Property="FontSize" Value="n"</c>) per file,
    /// for every XAML outside Styles/** and App.xaml (Views/, Controls/, MainWindow.xaml, any new folder).
    /// Files not listed have a baseline of zero. Total today: 184.</summary>
    private static readonly IReadOnlyDictionary<string, int> FontSizeLiteralBaseline = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Controls/DiscoveredServerCard.xaml"] = 5,
        ["Controls/EmptyStateControl.xaml"] = 3,
        ["Controls/ServerActionsButton.xaml"] = 1,
        ["Controls/ServerCompactCard.xaml"] = 15,
        ["Controls/ServerEditorModal.xaml"] = 1,
        ["Controls/ServerFormControl.xaml"] = 50,
        ["Controls/ServerFullCard.xaml"] = 21,
        ["MainWindow.xaml"] = 7,
        ["Views/BackupCreateDialog.xaml"] = 3,
        ["Views/DashboardPage.xaml"] = 8,
        ["Views/HistoryPage.xaml"] = 7,
        ["Views/RestoreConfirmDialog.xaml"] = 3,
        ["Views/RestoreOpenDialog.xaml"] = 2,
        ["Views/SettingsPage.xaml"] = 20,
        ["Views/WorkloadsPage.xaml"] = 38
    };

    /// <summary>References to the brand accent (<c>BrandAccent*</c> key or the <c>#1846E1</c> literal) outside
    /// Styles/**, per file. Counted in XAML attribute values and C# code (comments excluded). Files not listed
    /// have a baseline of zero.</summary>
    private static readonly IReadOnlyDictionary<string, int> BrandAccentBaseline = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Controls/ServerCompactCard.xaml"] = 3,
        ["Controls/ServerFullCard.xaml"] = 4,
        ["Views/HistoryPage.xaml"] = 3
    };

    [Fact]
    public void LiteralFontSizesOutsideStyles_DoNotGrow()
    {
        var counts = AppSourceTree.Files(".xaml")
            .Where(file => file != "App.xaml" && !AppSourceTree.IsUnderStyles(file))
            .ToDictionary(file => file, file => CountFontSizeLiterals(AppSourceTree.LoadXaml(file)), StringComparer.Ordinal);

        AssertNoGrowth(counts, FontSizeLiteralBaseline, "literal FontSize values (use a type-ramp style/token from Styles/**)");
    }

    [Fact]
    public void HexColourLiteralsOutsideStyles_StayAtZero()
    {
        var offenders = new List<string>();
        foreach (var file in AppSourceTree.Files(".xaml").Where(file => !AppSourceTree.IsUnderStyles(file)))
        {
            offenders.AddRange(XamlValues(AppSourceTree.LoadXaml(file))
                .SelectMany(value => HexColour().Matches(value))
                .Select(match => $"{file}: {match.Value}"));
        }

        foreach (var file in AppSourceTree.Files(".cs").Where(file => !AppSourceTree.IsUnderStyles(file)))
        {
            offenders.AddRange(StringLiteral().Matches(AppSourceTree.CodeWithoutComments(file))
                .SelectMany(literal => HexColour().Matches(literal.Value))
                .Select(match => $"{file}: {match.Value}"));
        }

        Assert.True(offenders.Count == 0,
            "Hex colour literals are only allowed in Styles/** (define a token and reference it):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void BrandAccentReferencesOutsideStyles_DoNotGrow()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in AppSourceTree.Files(".xaml").Where(file => !AppSourceTree.IsUnderStyles(file)))
        {
            counts[file] = XamlValues(AppSourceTree.LoadXaml(file)).Sum(value => BrandAccent().Matches(value).Count);
        }

        foreach (var file in AppSourceTree.Files(".cs").Where(file => !AppSourceTree.IsUnderStyles(file)))
        {
            counts[file] = BrandAccent().Matches(AppSourceTree.CodeWithoutComments(file)).Count;
        }

        AssertNoGrowth(counts, BrandAccentBaseline, "BrandAccent*/#1846E1 references (the accent is reserved; see §0)");
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

    /// <summary>Attribute values and element text; XML comments are never included.</summary>
    private static IEnumerable<string> XamlValues(XDocument document) =>
        document.Descendants().Attributes().Select(a => a.Value)
            .Concat(document.DescendantNodes().OfType<XText>().Select(t => t.Value));

    [GeneratedRegex(@"^\d+(\.\d+)?$")]
    private static partial Regex NumericLiteral();

    [GeneratedRegex(@"(?<![\w&])#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3,4})\b")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"BrandAccent\w*|#1846E1\b", RegexOptions.IgnoreCase)]
    private static partial Regex BrandAccent();

    [GeneratedRegex(@"^Sa[A-Z]\w*Brush$")]
    private static partial Regex SaBrushKey();

    [GeneratedRegex(@"""(?:\\.|[^""\\\n])*""")]
    private static partial Regex StringLiteral();
}
