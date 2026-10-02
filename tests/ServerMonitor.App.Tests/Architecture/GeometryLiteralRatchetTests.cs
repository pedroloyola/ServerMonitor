using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.3 gate 1B (UI-RATCHET-GEOMETRY): hardcoded <c>CornerRadius</c>/<c>Padding</c> literals in app XAML may only
/// go DOWN. Scope (aligned with UI.1 G-2): every <c>src/ServerMonitor.App/**/*.xaml</c> except bin/, obj/ and the
/// token/component layer <c>Styles/Tokens/**</c> + <c>Styles/Components/**</c>, where literals belong. The legacy
/// dictionaries <c>Styles/Controls.xaml</c> and <c>Styles/DesignTokens.xaml</c> are scanned and baselined like any
/// page; no other dictionary may live in Styles/ outside Tokens/ and Components/.
///
/// Forms caught: the attribute; <c>Setter Property|Target=".." Value=".."</c> (incl. <c>&lt;Setter.Value&gt;</c> and
/// VisualState <c>Target="X.Padding"</c>); the property element (<c>&lt;Border.CornerRadius&gt;</c>);
/// <c>ObjectAnimationUsingKeyFrames TargetProperty</c> + <c>DiscreteObjectKeyFrame</c> values; a keyed
/// <c>&lt;CornerRadius&gt;</c>, and a keyed <c>&lt;Thickness&gt;</c> that a Padding consumes anywhere in the app or whose
/// key names a padding (lightweight styling, e.g. <c>ButtonPadding</c>). Markup extensions (<c>{StaticResource}</c>,
/// <c>{ThemeResource}</c>, <c>{x:Bind}</c>, <c>{Binding}</c>, <c>{TemplateBinding}</c>) are fine.
///
/// Two versioned ledgers next to this file, keyed by file + property + value with an exact count:
/// <list type="bullet">
/// <item><c>GeometryLiteralBaseline.tsv</c> - the debt recorded at UI.3 start. It only shrinks: remove debt, then lower
/// or delete the entry in the same PR (a stale entry fails the build). Never add or raise an entry.</item>
/// <item><c>GeometryLiteralAllowlist.tsv</c> - authorised exceptions, each with a reason, one exact key per line (no
/// wildcards). Adding an entry needs Boss approval; it is ratcheted exactly like the baseline.</item>
/// </list>
/// Vigil M-1B-1: each ledger's per-property total is also pinned in <see cref="PinnedLedgerTotals"/>, so raising a
/// .tsv cannot pass without editing this file (visible in review); the pins move down with the ledgers.
/// </summary>
public sealed partial class GeometryLiteralRatchetTests
{
    private static readonly string LedgerFolder =
        Path.Combine(AppSourceTree.RepositoryRoot, "tests", "ServerMonitor.App.Tests", "Architecture");

    internal static string BaselinePath { get; } = Path.Combine(LedgerFolder, "GeometryLiteralBaseline.tsv");

    internal static string AllowlistPath { get; } = Path.Combine(LedgerFolder, "GeometryLiteralAllowlist.tsv");

    /// <summary>
    /// Exact per-ledger, per-property totals. ONLY EVER LOWER these, in the same PR that shrinks the .tsv (the test
    /// names the new value). Raising one is a policy change that needs Boss approval. UI.3 start: baseline 22
    /// CornerRadius + 49 Padding (incl. the legacy Styles/Controls.xaml + Styles/DesignTokens.xaml), allowlist 50 Padding.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Ledger, string Property), int> PinnedLedgerTotals =
        new Dictionary<(string Ledger, string Property), int>
        {
            [("GeometryLiteralBaseline.tsv", "CornerRadius")] = 22,
            [("GeometryLiteralBaseline.tsv", "Padding")] = 43,
            [("GeometryLiteralAllowlist.tsv", "CornerRadius")] = 0,
            [("GeometryLiteralAllowlist.tsv", "Padding")] = 50
        };

    /// <summary>The only dictionaries allowed directly in Styles/ (legacy, baselined, only shrink - UI.1 G-2).</summary>
    internal static readonly IReadOnlySet<string> LegacyStyleDictionaries =
        new HashSet<string>(StringComparer.Ordinal) { "Styles/Controls.xaml", "Styles/DesignTokens.xaml" };

    /// <summary>UI.2 T-8 zero-debt folders: a literal there is a defect, never recorded debt (allowlist only, with reason).</summary>
    private static readonly string[] ZeroDebtFolders = ["Controls/Primitives/", "Qa/"];

    [Fact]
    public void GeometryLiteralsOutsideTheTokenLayer_DoNotGrow()
    {
        var literals = ScanTree();
        var baseline = ReadLedger(BaselinePath, withReason: false);
        var allowlist = ReadLedger(AllowlistPath, withReason: true);
        var suggestions = TokenSuggestions.FromStyles();

        var failures = literals
            .GroupBy(literal => literal.Key)
            .Where(group => group.Count() > Allowed(group.Key))
            .OrderBy(group => group.Key.File, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Property, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Value, StringComparer.Ordinal)
            .SelectMany(group => group.Select(literal =>
                $"  {literal.File}:{literal.Line}  {literal.Property}=\"{literal.Value}\" ({literal.Form})" +
                $"  [{group.Count()} in file > {Allowed(group.Key)} recorded]  -> {suggestions.For(literal.Property, literal.Value)}"))
            .ToList();

        Assert.True(failures.Count == 0,
            "New hardcoded geometry in app XAML outside Styles/Tokens/** and Styles/Components/** - the UI.3 geometry ratchet only goes down:" +
            Environment.NewLine + string.Join(Environment.NewLine, failures) + Environment.NewLine +
            "Reference a token or a Styles/Components/** style instead. Do NOT add or raise a GeometryLiteralBaseline.tsv entry; " +
            "a genuine exception needs a GeometryLiteralAllowlist.tsv entry with a reason and Boss approval.");

        int Allowed(GeometryKey key) =>
            (baseline.TryGetValue(key, out var recorded) ? recorded.Count : 0) + (allowlist.TryGetValue(key, out var allowed) ? allowed.Count : 0);
    }

    [Fact]
    public void GeometryLedgers_HaveNoObsoleteEntries()
    {
        var actual = ScanTree().GroupBy(literal => literal.Key).ToDictionary(group => group.Key, group => group.Count());
        var stale = new List<string>();

        foreach (var (path, withReason) in new[] { (BaselinePath, false), (AllowlistPath, true) })
        {
            foreach (var entry in ReadLedger(path, withReason).Values)
            {
                var count = actual.GetValueOrDefault(entry.Key);
                if (count < entry.Count)
                {
                    stale.Add(count == 0
                        ? $"  {Path.GetFileName(path)}:{entry.Line}  {entry.Key} = {entry.Count}, tree has 0 -> DELETE this line"
                        : $"  {Path.GetFileName(path)}:{entry.Line}  {entry.Key} = {entry.Count}, tree has {count} -> lower the count to {count}");
                }
            }
        }

        Assert.True(stale.Count == 0,
            "Geometry debt was removed but the ledger was not shrunk (the ratchet only goes down - lock the win in):" +
            Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>Vigil M-1B-1: a .tsv cannot be raised without editing the pinned totals in this file.</summary>
    [Fact]
    public void GeometryLedgerTotals_MatchThePinnedTotals()
    {
        var problems = new List<string>();
        foreach (var (path, withReason) in new[] { (BaselinePath, false), (AllowlistPath, true) })
        {
            var ledger = Path.GetFileName(path);
            var entries = ReadLedger(path, withReason).Values.ToList();
            foreach (var property in GeometryLiteralScanner.Properties)
            {
                var total = entries.Where(entry => entry.Key.Property == property).Sum(entry => entry.Count);
                var pinned = PinnedLedgerTotals[(ledger, property)];
                if (total > pinned)
                {
                    problems.Add($"  {ledger} {property} total {total} > pinned {pinned}: the ledger was RAISED. Ledgers only go down; " +
                        "an exception is a policy change (Boss approval) that must edit PinnedLedgerTotals in GeometryLiteralRatchetTests.cs.");
                }
                else if (total < pinned)
                {
                    problems.Add($"  {ledger} {property} total {total} < pinned {pinned}: lower PinnedLedgerTotals[(\"{ledger}\", \"{property}\")] to {total} in this PR.");
                }
            }
        }

        Assert.True(problems.Count == 0, "Geometry ledger totals drifted from the pinned totals:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void GeometryLedgers_AreWellFormedAndDisjoint()
    {
        var baseline = ReadLedger(BaselinePath, withReason: false);
        var allowlist = ReadLedger(AllowlistPath, withReason: true);
        var problems = new List<string>();

        problems.AddRange(baseline.Keys.Intersect(allowlist.Keys)
            .Select(key => $"{key} is in both the baseline and the allowlist - keep exactly one"));
        problems.AddRange(baseline.Values.Concat(allowlist.Values)
            .Where(entry => !InScope(entry.Key.File))
            .Select(entry => $"{entry.Key}: Styles/Tokens/** and Styles/Components/** are out of scope and must not appear in a geometry ledger"));
        problems.AddRange(baseline.Values
            .Where(entry => ZeroDebtFolders.Any(folder => entry.Key.File.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => $"{entry.Key}: zero-debt folder (UI.2 T-8) - fix it, or allowlist it with a reason; never baseline it"));

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Vigil M-1B-2 / UI.1 G-2: Styles/ holds the exempt token and component layers plus the two legacy (scanned)
    /// dictionaries - nothing else, so a new dictionary cannot launder literals next to them.
    /// </summary>
    [Fact]
    public void StylesHoldOnlyTheTokenAndComponentLayersAndTheLegacyDictionaries()
    {
        var strays = AppSourceTree.Files(".xaml")
            .Where(file => AppSourceTree.IsUnderStyles(file) && InScope(file) && !LegacyStyleDictionaries.Contains(file))
            .ToList();

        Assert.True(strays.Count == 0,
            "New XAML in Styles/ outside Styles/Tokens/** and Styles/Components/** (only the legacy Styles/Controls.xaml and " +
            "Styles/DesignTokens.xaml may live there): move it into Styles/Components/** (zero-debt) or Styles/Tokens/**:" +
            Environment.NewLine + string.Join(Environment.NewLine, strays));
    }

    [Theory]
    [InlineData("7", true)]
    [InlineData(" 12,8 ", true)]
    [InlineData("24,24,24,20", true)]
    [InlineData("1.5", true)]
    [InlineData("{}7", true)]
    [InlineData("{StaticResource SaRadiusRect}", false)]
    [InlineData("{ThemeResource ControlCornerRadius}", false)]
    [InlineData("{x:Bind ViewModel.Radius, Mode=OneWay}", false)]
    [InlineData("{Binding Padding}", false)]
    [InlineData("{TemplateBinding CornerRadius}", false)]
    [InlineData(" {StaticResource PagePadding}", false)]
    [InlineData("", false)]
    public void LiteralClassification(string value, bool expected) =>
        Assert.Equal(expected, GeometryLiteralScanner.IsLiteral(value));

    [Theory]
    [InlineData("Views/HistoryPage.xaml", true)]
    [InlineData("App.xaml", true)]
    [InlineData("Styles/Controls.xaml", true)]
    [InlineData("Styles/DesignTokens.xaml", true)]
    [InlineData("Styles/NewDictionary.xaml", true)]
    [InlineData("Styles/Legacy/Other.xaml", true)]
    [InlineData("Styles/Tokens/Radius.xaml", false)]
    [InlineData("Styles/Components/Sa.Primitives.xaml", false)]
    public void ScopeExemptsOnlyTheTokenAndComponentLayers(string file, bool expected) =>
        Assert.Equal(expected, InScope(file));

    [Fact]
    public void ScannerCatchesEveryXamlForm()
    {
        const string xaml = """
            <Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Page.Resources>
                    <CornerRadius x:Key="LocalRadius">5</CornerRadius>
                    <Thickness x:Key="LocalPadding">3,2</Thickness>
                    <Thickness x:Key="UnrelatedMargin">9</Thickness>
                    <Thickness x:Key="ButtonPadding">11,5</Thickness>
                    <Thickness x:Key="SetterValuePad">6,6</Thickness>
                    <Style x:Key="S" TargetType="Button">
                        <Setter Property="CornerRadius" Value="8" />
                        <Setter Property="Padding" Value="{StaticResource PagePadding}" />
                        <Setter Property="Padding">
                            <Setter.Value>
                                <Thickness>10,4</Thickness>
                            </Setter.Value>
                        </Setter>
                        <Setter Property="Margin">
                            <Setter.Value><StaticResource ResourceKey="UnrelatedMargin" /></Setter.Value>
                        </Setter>
                    </Style>
                    <Style x:Key="T" TargetType="Border">
                        <Setter Property="Padding">
                            <Setter.Value><StaticResource ResourceKey="SetterValuePad" /></Setter.Value>
                        </Setter>
                    </Style>
                </Page.Resources>
                <StackPanel Padding="{StaticResource LocalPadding}" Margin="9">
                    <VisualStateManager.VisualStateGroups>
                        <VisualStateGroup>
                            <VisualState x:Name="Narrow">
                                <VisualState.Setters>
                                    <Setter Target="Card.Padding" Value="7,3" />
                                    <Setter Target="Card.CornerRadius" Value="{StaticResource SaRadiusRect}" />
                                </VisualState.Setters>
                                <Storyboard>
                                    <ObjectAnimationUsingKeyFrames Storyboard.TargetName="Card" Storyboard.TargetProperty="Padding">
                                        <DiscreteObjectKeyFrame KeyTime="0" Value="2,1" />
                                    </ObjectAnimationUsingKeyFrames>
                                    <ObjectAnimationUsingKeyFrames Storyboard.TargetName="Card" Storyboard.TargetProperty="(Border.CornerRadius)">
                                        <DiscreteObjectKeyFrame KeyTime="0">
                                            <DiscreteObjectKeyFrame.Value><CornerRadius>9</CornerRadius></DiscreteObjectKeyFrame.Value>
                                        </DiscreteObjectKeyFrame>
                                    </ObjectAnimationUsingKeyFrames>
                                    <ObjectAnimationUsingKeyFrames Storyboard.TargetName="Card" Storyboard.TargetProperty="Margin">
                                        <DiscreteObjectKeyFrame KeyTime="0" Value="4" />
                                    </ObjectAnimationUsingKeyFrames>
                                </Storyboard>
                            </VisualState>
                        </VisualStateGroup>
                    </VisualStateManager.VisualStateGroups>
                    <Border x:Name="Card" CornerRadius="7" Padding="12,8" />
                    <Border CornerRadius="{StaticResource SaRadiusRect}" Padding="{ThemeResource P}" />
                    <Border>
                        <Border.CornerRadius>2,2,0,0</Border.CornerRadius>
                        <Border.Padding><StaticResource ResourceKey="P" /></Border.Padding>
                    </Border>
                </StackPanel>
            </Page>
            """;

        var document = XDocument.Parse(xaml, LoadOptions.SetLineInfo);
        var found = GeometryLiteralScanner.Scan("Views/Synthetic.xaml", document, GeometryLiteralScanner.PaddingKeysConsumed(document).ToHashSet())
            .Select(literal => $"{literal.Line}:{literal.Property}={literal.Value}/{literal.Form}")
            .Order(StringComparer.Ordinal)
            .ToList();

        string[] expected =
        [
            "4:CornerRadius=5/local-resource",
            "5:Padding=3,2/local-resource",
            "7:Padding=11,5/local-resource",
            "8:Padding=6,6/local-resource",
            "10:CornerRadius=8/setter",
            "12:Padding=10,4/setter",
            "32:Padding=7,3/visual-state-setter",
            "37:Padding=2,1/animation",
            "40:CornerRadius=9/animation",
            "51:CornerRadius=7/attribute",
            "51:Padding=12,8/attribute",
            "54:CornerRadius=2,2,0,0/property-element"
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), found);
    }

    [Theory]
    [InlineData("CornerRadius", "14", "SaRadiusRect")]
    [InlineData("CornerRadius", "12,12,12,12", "SaRadiusControl")]
    [InlineData("CornerRadius", "7", "no Sa* CornerRadius token equals 7")]
    [InlineData("Padding", "12,8", "no Sa* padding token")]
    public void FailureMessageSuggestsTheMatchingToken(string property, string value, string expectedFragment) =>
        Assert.Contains(expectedFragment, TokenSuggestions.FromStyles().For(property, value), StringComparison.Ordinal);

    internal static bool InScope(string relative) =>
        !relative.StartsWith("Styles/Tokens/", StringComparison.OrdinalIgnoreCase)
        && !relative.StartsWith("Styles/Components/", StringComparison.OrdinalIgnoreCase);

    internal static List<GeometryLiteral> ScanTree()
    {
        var documents = AppSourceTree.Files(".xaml")
            .ToDictionary(file => file, file => XDocument.Load(AppSourceTree.Full(file), LoadOptions.SetLineInfo), StringComparer.Ordinal);

        // A Thickness resource is padding geometry when ANY app XAML (token layer included) consumes it as a Padding.
        var paddingKeys = documents.Values.SelectMany(GeometryLiteralScanner.PaddingKeysConsumed).ToHashSet(StringComparer.Ordinal);

        return documents
            .Where(pair => InScope(pair.Key))
            .SelectMany(pair => GeometryLiteralScanner.Scan(pair.Key, pair.Value, paddingKeys))
            .ToList();
    }

    /// <summary>
    /// Reads a tab-separated ledger: <c>file  property  value  count</c> (+ <c>reason</c> for the allowlist).
    /// <c>#</c> lines and blank lines are ignored. Malformed lines, duplicate keys, non-positive counts, wildcards,
    /// unknown properties and missing reasons all throw, so a broken ledger can never silently allow debt.
    /// </summary>
    internal static Dictionary<GeometryKey, LedgerEntry> ReadLedger(string path, bool withReason)
    {
        var entries = new Dictionary<GeometryKey, LedgerEntry>();
        var lines = File.ReadAllLines(path);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var where = $"{Path.GetFileName(path)}:{index + 1}";
            var columns = line.Split('\t');
            if (columns.Length != (withReason ? 5 : 4))
            {
                throw new FormatException($"{where}: expected {(withReason ? 5 : 4)} tab-separated columns, got {columns.Length}.");
            }

            var key = new GeometryKey(columns[0], columns[1], GeometryLiteralScanner.Normalize(columns[2]));
            if (key.File.IndexOfAny(['*', '?']) >= 0 || !key.File.EndsWith(".xaml", StringComparison.Ordinal) || key.File.Contains('\\'))
            {
                throw new FormatException($"{where}: '{key.File}' must be one exact app-relative .xaml path with '/' (no wildcards).");
            }

            if (!GeometryLiteralScanner.Properties.Contains(key.Property))
            {
                throw new FormatException($"{where}: unknown property '{key.Property}'.");
            }

            if (!int.TryParse(columns[3], out var count) || count <= 0)
            {
                throw new FormatException($"{where}: count must be a positive integer, got '{columns[3]}'.");
            }

            if (withReason && columns[4].Trim().Length < 20)
            {
                throw new FormatException($"{where}: every allowlist entry needs a real reason (>= 20 characters).");
            }

            if (!entries.TryAdd(key, new LedgerEntry(key, count, index + 1)))
            {
                throw new FormatException($"{where}: duplicate entry {key}.");
            }
        }

        return entries;
    }

    internal sealed record LedgerEntry(GeometryKey Key, int Count, int Line);
}

internal readonly record struct GeometryKey(string File, string Property, string Value)
{
    public override string ToString() => $"{File} | {Property} | {Value}";
}

internal sealed record GeometryLiteral(string File, int Line, string Property, string Form, string Value)
{
    public GeometryKey Key => new(File, Property, Value);
}

/// <summary>Finds literal CornerRadius/Padding geometry in one XAML document (comments are never scanned).</summary>
internal static partial class GeometryLiteralScanner
{
    public static readonly string[] Properties = ["CornerRadius", "Padding"];

    /// <param name="paddingKeys">Resource keys some Padding consumes (see <see cref="PaddingKeysConsumed"/>).</param>
    public static IEnumerable<GeometryLiteral> Scan(string file, XDocument document, IReadOnlySet<string> paddingKeys)
    {
        var results = new List<GeometryLiteral>();
        var consumed = new HashSet<XElement>();

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes().Where(a => a.Name.Namespace == XNamespace.None))
            {
                if (PropertyOf(attribute.Name.LocalName) is { } property && IsLiteral(attribute.Value))
                {
                    results.Add(new(file, LineOf(attribute), property, "attribute", Normalize(attribute.Value)));
                }
            }

            if (element.Name.LocalName == "Setter" && SetterProperty(element) is { } setterProperty)
            {
                var value = element.Attribute("Value") is { } valueAttribute
                    ? (IsLiteral(valueAttribute.Value) ? valueAttribute.Value : null)
                    : element.Elements().FirstOrDefault(e => e.Name.LocalName == "Setter.Value") is { } setterValue
                        ? LiteralContent(setterValue, consumed)
                        : null;
                if (value is not null)
                {
                    var form = element.Attribute("Target") is not null ? "visual-state-setter" : "setter";
                    results.Add(new(file, LineOf(element), setterProperty, form, Normalize(value)));
                }
            }

            if (element.Name.LocalName.Contains('.') && PropertyOf(element.Name.LocalName) is { } elementProperty
                && LiteralContent(element, consumed) is { } content)
            {
                results.Add(new(file, LineOf(element), elementProperty, "property-element", Normalize(content)));
            }

            if (element.Name.LocalName == "ObjectAnimationUsingKeyFrames" && AnimatedProperty(element) is { } animatedProperty)
            {
                foreach (var frame in element.Elements().Where(e => e.Name.LocalName == "DiscreteObjectKeyFrame"))
                {
                    var value = frame.Attribute("Value") is { } valueAttribute
                        ? (IsLiteral(valueAttribute.Value) ? valueAttribute.Value : null)
                        : frame.Elements().FirstOrDefault(e => e.Name.LocalName == "DiscreteObjectKeyFrame.Value") is { } frameValue
                            ? LiteralContent(frameValue, consumed)
                            : null;
                    if (value is not null)
                    {
                        results.Add(new(file, LineOf(frame), animatedProperty, "animation", Normalize(value)));
                    }
                }
            }
        }

        // A keyed resource is the same literal one indirection away. CornerRadius resources are always geometry; a
        // Thickness resource counts when a Padding consumes it (anywhere in the app) or its key names a padding
        // (lightweight-styling keys such as ButtonPadding are consumed by a control template, not by this file).
        foreach (var element in document.Descendants().Where(e => !consumed.Contains(e)))
        {
            if (element.Attribute(AppSourceTree.Xaml + "Key") is not { } key || LiteralText(element) is not { } text)
            {
                continue;
            }

            if (element.Name.LocalName == "CornerRadius")
            {
                results.Add(new(file, LineOf(element), "CornerRadius", "local-resource", Normalize(text)));
            }
            else if (element.Name.LocalName == "Thickness"
                && (paddingKeys.Contains(key.Value) || key.Value.Contains("Padding", StringComparison.Ordinal)))
            {
                results.Add(new(file, LineOf(element), "Padding", "local-resource", Normalize(text)));
            }
        }

        return results;
    }

    /// <summary>Keys referenced (markup or <c>&lt;StaticResource ResourceKey&gt;</c>) where a Padding is set.</summary>
    public static IEnumerable<string> PaddingKeysConsumed(XDocument document)
    {
        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes().Where(a => a.Name.Namespace == XNamespace.None && PropertyOf(a.Name.LocalName) == "Padding"))
            {
                foreach (var key in MarkupKeys(attribute.Value))
                {
                    yield return key;
                }
            }

            var isPaddingScope =
                (element.Name.LocalName == "Setter" && SetterProperty(element) == "Padding")
                || (element.Name.LocalName.Contains('.') && PropertyOf(element.Name.LocalName) == "Padding")
                || (element.Name.LocalName == "ObjectAnimationUsingKeyFrames" && AnimatedProperty(element) == "Padding");
            if (!isPaddingScope)
            {
                continue;
            }

            foreach (var inner in element.DescendantsAndSelf())
            {
                foreach (var attribute in inner.Attributes().Where(a => a.Name.LocalName is "Value" or "ResourceKey"))
                {
                    if (attribute.Name.LocalName == "ResourceKey" && inner.Name.LocalName is "StaticResource" or "ThemeResource")
                    {
                        yield return attribute.Value;
                    }
                    else
                    {
                        foreach (var key in MarkupKeys(attribute.Value))
                        {
                            yield return key;
                        }
                    }
                }
            }
        }
    }

    /// <summary>True for a hard-coded value; false for any markup extension. <c>{}</c> is the XAML escape for a literal.</summary>
    public static bool IsLiteral(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0 && (trimmed.StartsWith("{}", StringComparison.Ordinal) || !trimmed.StartsWith('{'));
    }

    /// <summary>Whitespace-free, escape-free form, so "12, 8" and "12,8" are one ledger key.</summary>
    public static string Normalize(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("{}", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        return Whitespace().Replace(trimmed, string.Empty);
    }

    /// <summary>"Padding", "Border.Padding", "Card.Padding" (VisualState Target) and "(Border.Padding)" all map to Padding.</summary>
    private static string? PropertyOf(string name)
    {
        var bare = name.Trim().Trim('(', ')');
        var local = bare[(bare.LastIndexOf('.') + 1)..];
        return Properties.Contains(local) ? local : null;
    }

    /// <summary>A style Setter's <c>Property</c>, or a VisualState Setter's <c>Target</c> (<c>Element.Property</c>).</summary>
    private static string? SetterProperty(XElement setter) =>
        ((string?)setter.Attribute("Property") ?? (string?)setter.Attribute("Target")) is { } name ? PropertyOf(name) : null;

    private static string? AnimatedProperty(XElement animation) =>
        animation.Attributes().FirstOrDefault(a => a.Name.LocalName == "Storyboard.TargetProperty") is { } target
            ? PropertyOf(target.Value)
            : null;

    private static IEnumerable<string> MarkupKeys(string value) =>
        ResourceKey().Matches(value).Select(match => match.Groups[1].Value);

    /// <summary>Literal content of a property element: text, or a single CornerRadius/Thickness object element.</summary>
    private static string? LiteralContent(XElement container, HashSet<XElement> consumed)
    {
        var children = container.Elements().ToList();
        if (children.Count == 0)
        {
            var text = container.Value.Trim();
            return IsLiteral(text) ? text : null;
        }

        if (children.Count == 1 && children[0].Name.LocalName is "CornerRadius" or "Thickness")
        {
            consumed.Add(children[0]);
            return LiteralText(children[0]);
        }

        return null;
    }

    /// <summary>A CornerRadius/Thickness object element's literal value: its text, or its literal side attributes.</summary>
    private static string? LiteralText(XElement element)
    {
        if (element.Name.LocalName is not ("CornerRadius" or "Thickness"))
        {
            return null;
        }

        var text = element.Value.Trim();
        if (text.Length > 0)
        {
            return IsLiteral(text) ? text : null;
        }

        var sides = element.Attributes().Where(a => a.Name.Namespace == XNamespace.None && IsLiteral(a.Value))
            .Select(a => $"{a.Name.LocalName}={a.Value.Trim()}").ToList();
        return sides.Count > 0 ? string.Join(";", sides) : null;
    }

    private static int LineOf(IXmlLineInfo node) => node.HasLineInfo() ? node.LineNumber : 0;

    [GeneratedRegex(@"\{(?:StaticResource|ThemeResource)\s+(?:ResourceKey\s*=\s*)?([^\s,}]+)")]
    private static partial Regex ResourceKey();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Maps a literal to the Sa* token that already holds that value, read live from Styles/Tokens|Components.</summary>
internal sealed class TokenSuggestions
{
    private readonly ILookup<string, string> radius;
    private readonly ILookup<string, string> padding;

    private TokenSuggestions(ILookup<string, string> radius, ILookup<string, string> padding)
    {
        this.radius = radius;
        this.padding = padding;
    }

    public static TokenSuggestions FromStyles()
    {
        var tokens = AppSourceTree.Files(".xaml").Where(file => AppSourceTree.IsUnderStyles(file) && !GeometryLiteralRatchetTests.InScope(file))
            .SelectMany(file => AppSourceTree.LoadXaml(file).Descendants())
            .Where(e => e.Attribute(AppSourceTree.Xaml + "Key")?.Value.StartsWith("Sa", StringComparison.Ordinal) == true)
            .Select(e => (Type: e.Name.LocalName, Key: e.Attribute(AppSourceTree.Xaml + "Key")!.Value, Value: Canonical(e.Value)))
            .Distinct()
            .ToList();

        return new TokenSuggestions(
            tokens.Where(t => t.Type == "CornerRadius").ToLookup(t => t.Value, t => t.Key),
            tokens.Where(t => t.Type == "Thickness" && t.Key.Contains("Padding", StringComparison.Ordinal)).ToLookup(t => t.Value, t => t.Key));
    }

    public string For(string property, string value)
    {
        var canonical = Canonical(value);
        if (property == "CornerRadius")
        {
            return radius[canonical].Any()
                ? "use {StaticResource " + string.Join("} or {StaticResource ", radius[canonical].Order(StringComparer.Ordinal)) + "}"
                : $"no Sa* CornerRadius token equals {value}: use the nearest SaRadius* in Styles/Tokens/Radius.xaml (design call) or add one there first";
        }

        return padding[canonical].Any()
            ? "use {StaticResource " + string.Join("} or {StaticResource ", padding[canonical].Order(StringComparer.Ordinal)) + "}"
            : "no Sa* padding token for this value: move it into a Styles/Components/** style, or add a Sa*Padding Thickness token in Styles/Tokens/** first";
    }

    /// <summary>"12", "12,12" and "12,12,12,12" are the same uniform value.</summary>
    private static string Canonical(string value)
    {
        var parts = GeometryLiteralScanner.Normalize(value).Split(',');
        return parts.Distinct().Count() == 1 ? parts[0] : string.Join(",", parts);
    }
}
