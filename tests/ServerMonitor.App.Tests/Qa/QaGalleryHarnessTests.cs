using System.Xml.Linq;
using ServerMonitor.App.Qa.Gallery;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.App.Tests.Qa;

/// <summary>
/// UI.2 S2 Debug-only gallery: T-1 (the token manifest is exactly the token layer), T-3 (the probe page renders
/// exactly the manifest), and the gallery's own data boundary (off by default, never the real data folder).
/// </summary>
public sealed class QaGalleryHarnessTests
{
    /// <summary>T-1. Fails both ways: a token without a manifest entry, or an entry for a token that no longer exists.</summary>
    [Fact]
    public void ManifestMatchesEveryTokenKey()
    {
        var tokenKeys = AppSourceTree.Files(".xaml")
            .Where(file => file.StartsWith("Styles/Tokens/", StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => AppSourceTree.LoadXaml(file).Descendants().Attributes(AppSourceTree.Xaml + "Key").Select(a => a.Value))
            .Where(key => key.StartsWith("Sa", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(QaTokenManifest.Keys.Count, QaTokenManifest.Keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(tokenKeys.Except(QaTokenManifest.Keys).OrderBy(k => k, StringComparer.Ordinal).Select(k => "token without manifest entry: " + k));
        Assert.Empty(QaTokenManifest.Keys.Except(tokenKeys).OrderBy(k => k, StringComparer.Ordinal).Select(k => "manifest entry without token: " + k));
    }

    /// <summary>The probe kind is what the self-check reads back, so it must match the element that defines the key.</summary>
    [Fact]
    public void ManifestKindsMatchTheTokenElements()
    {
        var elements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in AppSourceTree.Files(".xaml").Where(f => f.StartsWith("Styles/Tokens/", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var element in AppSourceTree.LoadXaml(file).Descendants().Where(e => e.Attribute(AppSourceTree.Xaml + "Key") is not null))
            {
                elements.TryAdd(element.Attribute(AppSourceTree.Xaml + "Key")!.Value, element.Name.LocalName);
            }
        }

        var mismatches = QaTokenManifest.Entries
            .Where(entry => KindOf(elements[entry.Key]) != entry.Kind)
            .Select(entry => $"{entry.Key}: manifest {entry.Kind}, element <{elements[entry.Key]}>")
            .ToList();
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));

        static QaTokenKind KindOf(string element) => element switch
        {
            "Color" => QaTokenKind.Color,
            "SolidColorBrush" or "AcrylicBrush" => QaTokenKind.Brush,
            "ThemeShadow" => QaTokenKind.Shadow,
            "FontFamily" => QaTokenKind.FontFamily,
            "Double" => QaTokenKind.Double,
            "Style" => QaTokenKind.Style,
            "Thickness" => QaTokenKind.Thickness,
            "CornerRadius" => QaTokenKind.CornerRadius,
            "String" => QaTokenKind.MotionString,
            _ => throw new InvalidOperationException("Unexpected token element " + element)
        };
    }

    /// <summary>
    /// T-3. Exactly one probe (Tag = key) per non-colour manifest key; brushes probed through ThemeResource (so a
    /// theme switch re-resolves them), everything else through StaticResource on the SAME element. Raw colours
    /// have no XAML probe by design: SaColor* is private to Styles/Tokens/**.
    /// </summary>
    [Fact]
    public void QaTokenProbePageCoversManifest()
    {
        var probes = AppSourceTree.LoadXaml("Qa/Gallery/QaTokenProbePage.xaml").Descendants()
            .Where(e => ((string?)e.Attribute("Tag"))?.StartsWith("Sa", StringComparison.Ordinal) == true)
            .ToList();
        var tags = probes.Select(e => (string)e.Attribute("Tag")!).ToList();

        Assert.Equal(tags.Count, tags.Distinct(StringComparer.Ordinal).Count());
        var expected = QaTokenManifest.Entries.Where(e => e.Kind != QaTokenKind.Color).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(expected.Except(tags).Select(k => "manifest key without probe: " + k));
        Assert.Empty(tags.Except(expected).Select(k => "probe without (non-colour) manifest key: " + k));

        var failures = new List<string>();
        foreach (var probe in probes)
        {
            var key = (string)probe.Attribute("Tag")!;
            var kind = QaTokenManifest.Entries.Single(e => e.Key == key).Kind;
            var markup = kind == QaTokenKind.Brush ? "ThemeResource" : "StaticResource";
            if (!probe.Attributes().Any(a => a.Value == $"{{{markup} {key}}}"))
            {
                failures.Add($"{key}: probe must consume the key through {{{markup} {key}}}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.All(QaTokenManifest.Entries.Where(e => e.Kind == QaTokenKind.Color), e => Assert.StartsWith("SaColor", e.Key, StringComparison.Ordinal));
    }

    [Fact]
    public void GalleryIsNotRequestedByDefault()
    {
        // The test host is launched without --qa-components/--qa-tokens, so the real composition stays active.
        Assert.False(QaGalleryComposition.IsRequested());
    }

    [Fact]
    public void GalleryRealDataFolderIsTheApplicationsDataFolder()
    {
        var real = Path.GetDirectoryName(ServerStorageOptions.ForCurrentUser().FilePath)!;

        Assert.Equal(Path.GetFullPath(real), Path.GetFullPath(QaGalleryComposition.RealDataDirectory), ignoreCase: true);
        Assert.Equal(QaGalleryMode.Refused, QaGalleryPolicy.Resolve(
            ["x", QaGalleryPolicy.TokensFlag, QaGalleryPolicy.OutputFlag, real], isDebugBuild: true,
            QaGalleryComposition.RealDataDirectory, QaGalleryComposition.DefaultOutputDirectory).Mode);
    }

    [Fact]
    public void DefaultReportFolderIsPerProcessUnderTempNeverRealData()
    {
        var folder = Path.GetFullPath(QaGalleryComposition.DefaultOutputDirectory);

        Assert.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ServerMonitor-QA", "components")), folder, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), folder, StringComparison.Ordinal);
        Assert.False(folder.StartsWith(Path.GetFullPath(QaGalleryComposition.RealDataDirectory), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GalleryPagesMatchThePolicyPageIds()
    {
        var pages = AppSourceTree.Files(".xaml").Where(f => f.StartsWith("Qa/Gallery/", StringComparison.Ordinal) && f.EndsWith("Page.xaml", StringComparison.Ordinal)).ToList();

        // One page per policy id (the probe page backs the "tokens" id).
        Assert.Equal(QaGalleryPolicy.Pages.Count, pages.Count);
    }
}
