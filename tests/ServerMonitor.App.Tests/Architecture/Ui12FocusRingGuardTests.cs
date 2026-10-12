using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 FOCUS-01 (Prism UI12A): in High Contrast SaFocusRingBrush and the selected fills are both Highlight, and the Sa
/// styles put the ring ON the fill edge (FocusVisualMargin 0) - the ring vanished on the selected item (2.4.7 for HC
/// users). The system's inner (secondary) ring is restored in HC only, in the Window colour; Dark/Light keep thickness 0,
/// so the approved UI.10 F01 ring is unchanged there. Static guard; real HC stays NOT_RUN (Beacon R-6).
/// </summary>
public sealed class Ui12FocusRingGuardTests
{
    [Fact]
    public void EverySaStyleTakesTheSecondaryRingFromTheThemedTokens()
    {
        var failures = new List<string>();
        var styles = 0;
        foreach (var file in AppSourceTree.Files(".xaml").Where(f => f.StartsWith("Styles/", StringComparison.Ordinal)
                     || f.StartsWith("Views/", StringComparison.Ordinal) || f.StartsWith("Controls/", StringComparison.Ordinal)))
        {
            foreach (var style in AppSourceTree.LoadXaml(file).Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                var setters = style.Elements().Where(e => e.Name.LocalName == "Setter")
                    .ToDictionary(s => (string)s.Attribute("Property")!, s => (string?)s.Attribute("Value") ?? "", StringComparer.Ordinal);
                if (!setters.TryGetValue("FocusVisualSecondaryThickness", out var thickness))
                {
                    continue;
                }

                styles++;
                var key = (string?)style.Attribute(AppSourceTree.Xaml + "Key") ?? "<implicit>";
                if (thickness != "{ThemeResource SaFocusVisualSecondaryThickness}")
                {
                    failures.Add($"{file} {key}: FocusVisualSecondaryThickness = {thickness}");
                }

                if (!setters.TryGetValue("FocusVisualSecondaryBrush", out var brush) || brush != "{ThemeResource SaFocusRingSecondaryBrush}")
                {
                    failures.Add($"{file} {key}: FocusVisualSecondaryBrush must be SaFocusRingSecondaryBrush");
                }
            }
        }

        Assert.True(styles >= 13, $"Only {styles} styles set the secondary ring; the guard would be vacuous.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void SecondaryRingIsHighContrastOnly()
    {
        var thickness = ThemeEntries("Styles/Components/Sa.Surfaces.xaml", "SaFocusVisualSecondaryThickness", e => e.Value.Trim());
        Assert.Equal("0", thickness["Dark"]);
        Assert.Equal("0", thickness["Light"]);
        Assert.Equal("1", thickness["HighContrast"]);

        var brush = ThemeEntries("Styles/Tokens/Color.Semantic.xaml", "SaFocusRingSecondaryBrush", e => (string)e.Attribute("Color")!);
        Assert.Equal("{ThemeResource SystemColorWindowColor}", brush["HighContrast"]);
    }

    private static Dictionary<string, string> ThemeEntries(string file, string key, Func<XElement, string> value) =>
        AppSourceTree.LoadXaml(file).Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(AppSourceTree.Xaml + "Key") is not null)
            .ToDictionary(
                e => (string)e.Attribute(AppSourceTree.Xaml + "Key")!,
                e => value(e.Elements().Single(x => (string?)x.Attribute(AppSourceTree.Xaml + "Key") == key)),
                StringComparer.Ordinal);
}
