using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 B12B-01 (Beacon, real HC Night sky / Desert): icons on Highlight (checked sidebar item, selected segments, icon-only
/// buttons on hover / pressed) kept their rest colour, 1.44-1.78:1. Measured (Sentry recovery, Dark): the icon's Foreground
/// DOES follow its ContentPresenter by inheritance, but the template's {TemplateBinding Foreground} does not track inherited
/// changes. Fix = pure theme resources: the SaIcon template keeps today's TemplateBinding path for Light / Dark / Default and
/// adds a {Binding} twin that only the HighContrast dictionary shows. No code reacts to the system HC switch: 85a6b08 did
/// (AccessibilitySettings.HighContrastChanged), and that subscription throws 0x80070490 here and fail-fasted every launch.
/// Static guards; real HC ON / transition is a runtime check (Beacon).
/// </summary>
public sealed partial class Ui12HighContrastIconTests
{
    private const string Primitives = "Styles/Components/Sa.Primitives.xaml";

    [Fact]
    public void LightDarkAndDefaultDrawOnlyTodaysPath_HighContrastOnlyTheStatePath()
    {
        var themes = AppSourceTree.LoadXaml(Primitives).Root!
            .Elements().Single(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries")
            .Elements().ToDictionary(d => (string)d.Attribute(AppSourceTree.Xaml + "Key")!, StringComparer.Ordinal);

        Assert.Equal(["Dark", "Default", "HighContrast", "Light"], themes.Keys.Order(StringComparer.Ordinal));
        foreach (var theme in new[] { "Default", "Dark", "Light" })
        {
            Assert.Equal("Visible", Entry(themes[theme], "SaIconRestPathVisibility"));
            Assert.Equal("Collapsed", Entry(themes[theme], "SaIconStatePathVisibility"));
        }

        Assert.Equal("Collapsed", Entry(themes["HighContrast"], "SaIconRestPathVisibility"));
        Assert.Equal("Visible", Entry(themes["HighContrast"], "SaIconStatePathVisibility"));
    }

    [Fact]
    public void TodaysPathIsUnchanged_AndTheStatePathIsItsTwinThatTracksTheInheritedForeground()
    {
        var paths = IconTemplate().Descendants().Where(e => e.Name.LocalName == "Path")
            .ToDictionary(p => (string)p.Attribute(AppSourceTree.Xaml + "Name")!, StringComparer.Ordinal);
        Assert.Equal(["PART_Path", "PART_StatePath"], paths.Keys.Order(StringComparer.Ordinal));

        var rest = paths["PART_Path"];
        var state = paths["PART_StatePath"];
        // Light / Dark pixel-identical by construction: the rest path is exactly the pre-UI.12 one plus its theme visibility.
        Assert.Equal("{TemplateBinding Foreground}", (string?)rest.Attribute("Stroke"));
        Assert.Equal("{ThemeResource SaIconRestPathVisibility}", (string?)rest.Attribute("Visibility"));
        Assert.Equal("{Binding Foreground, RelativeSource={RelativeSource TemplatedParent}}", (string?)state.Attribute("Stroke"));
        Assert.Equal("{ThemeResource SaIconStatePathVisibility}", (string?)state.Attribute("Visibility"));

        foreach (var attribute in new[] { "StrokeThickness", "StrokeStartLineCap", "StrokeEndLineCap", "StrokeLineJoin" })
        {
            Assert.Equal((string?)rest.Attribute(attribute), (string?)state.Attribute(attribute));
        }

        Assert.Equal("1.5", (string?)rest.Attribute("StrokeThickness"));
        // Closed attribute list on BOTH paths (Atlas A-B12B-01: an Opacity / Fill / transform on the HC twin would hide or
        // change the HC icon while every other check passed).
        foreach (var path in new[] { rest, state })
        {
            Assert.Equal(
                ["Name", "Stroke", "StrokeEndLineCap", "StrokeLineJoin", "StrokeStartLineCap", "StrokeThickness", "Visibility"],
                path.Attributes().Select(a => a.Name.LocalName).Order(StringComparer.Ordinal));
            Assert.Empty(path.Elements());
        }
    }

    [Fact]
    public void BothPathsGetTheGeometry()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaIcon.cs");
        Assert.Contains("GetTemplateChild(StatePathPartName)", code, StringComparison.Ordinal);
        Assert.Contains("UpdateGeometry(_path);", code, StringComparison.Ordinal);
        Assert.Contains("UpdateGeometry(_statePath);", code, StringComparison.Ordinal);
        Assert.Contains("private const string StatePathPartName = \"PART_StatePath\";", code, StringComparison.Ordinal);
    }

    /// <summary>The measured crash class: subscribing AccessibilitySettings.HighContrastChanged throws 0x80070490 in this
    /// WinUI 3 desktop app (fail-fast 0xc000027b when it escapes a handler). Theme dictionaries handle HC; never the event.</summary>
    [Fact]
    public void NoAppCodeSubscribesTheSystemHighContrastEvent()
    {
        var offenders = AppSourceTree.Files(".cs")
            .Where(f => HighContrastEvent().IsMatch(AppSourceTree.CodeWithoutComments(f)))
            .ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
    }

    /// <summary>
    /// UI.12 B12B-01 follow-up (Prism P3): a LOCAL Foreground on an icon beats the inherited state text, so inside a host
    /// whose template swaps ContentPresenter.Foreground on hover / pressed / checked (Glass, Flat, nav item, segments) the
    /// icon stayed WindowText on Highlight in HC. Such icons take SaIconSecondaryStyle (a theme style whose HighContrast
    /// version sets no Foreground) instead of a local Foreground.
    /// </summary>
    [Fact]
    public void NoIconInsideAStateTextHostCarriesALocalForeground()
    {
        var hosts = StateTextHostStyles();
        Assert.Contains("SaGlassButtonTemplate", hosts.Templates);
        Assert.Contains("SaFlatButtonTemplate", hosts.Templates);
        foreach (var expected in new[] { "SaDisclosureButtonStyle", "SaPillButtonXSmallStyle", "SaPickerButtonStyle", "SaNavItemStyle", "SaSegmentedThemeItemStyle" })
        {
            Assert.Contains(expected, hosts.Styles);
        }

        var offenders = new List<string>();
        var checkedIcons = 0;
        foreach (var file in AppSourceTree.Files(".xaml").Where(f => f.StartsWith("Views/", StringComparison.Ordinal)
                     || f.StartsWith("Controls/", StringComparison.Ordinal) || f.StartsWith("Styles/", StringComparison.Ordinal)))
        {
            foreach (var icon in AppSourceTree.LoadXaml(file).Descendants().Where(e => e.Name.LocalName == "SaIcon"))
            {
                var host = icon.Ancestors().FirstOrDefault(a => a.Name.LocalName is "Button" or "RadioButton" or "ToggleButton");
                var key = host is null ? null : StaticKey((string?)host.Attribute("Style"));
                if (key is null || !hosts.Styles.Contains(key))
                {
                    continue;
                }

                checkedIcons++;
                if (icon.Attribute("Foreground") is not null)
                {
                    offenders.Add($"{file} {(string?)icon.Attribute("Data")} ({key})");
                }
            }
        }

        Assert.True(checkedIcons >= 15, $"Only {checkedIcons} icons inside state-text hosts; the guard would be vacuous.");
        Assert.True(offenders.Count == 0, "Local Foreground on an icon inside a state-text host: " + string.Join("; ", offenders));
    }

    [Fact]
    public void TheSecondaryIconStyleSetsItsColourEverywhereButHighContrast()
    {
        var themes = AppSourceTree.LoadXaml(Primitives).Root!
            .Elements().Single(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries")
            .Elements().ToDictionary(d => (string)d.Attribute(AppSourceTree.Xaml + "Key")!, StringComparer.Ordinal);
        foreach (var (theme, dictionary) in themes)
        {
            var style = dictionary.Elements().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "SaIconSecondaryStyle");
            Assert.Equal("primitives:SaIcon", (string?)style.Attribute("TargetType"));
            Assert.Equal("{StaticResource SaIconBaseStyle}", (string?)style.Attribute("BasedOn"));
            var setters = style.Elements().Select(e => ((string?)e.Attribute("Property"), (string?)e.Attribute("Value"))).ToList();
            if (theme == "HighContrast")
            {
                Assert.Empty(setters);
            }
            else
            {
                Assert.Equal([("Foreground", "{ThemeResource SaTextSecondaryBrush}")], setters);
            }
        }

        // The implicit icon style is the keyed base (template included), so an explicit SaIconSecondaryStyle keeps the template.
        var implicitStyle = AppSourceTree.LoadXaml(Primitives).Root!.Elements()
            .Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "primitives:SaIcon" && e.Attribute(AppSourceTree.Xaml + "Key") is null);
        Assert.Equal("{StaticResource SaIconBaseStyle}", (string?)implicitStyle.Attribute("BasedOn"));
        Assert.Empty(implicitStyle.Elements());
    }

    private static (HashSet<string> Styles, HashSet<string> Templates) StateTextHostStyles()
    {
        var templates = new HashSet<string>(StringComparer.Ordinal);
        var styles = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var file in AppSourceTree.Files(".xaml").Where(f => f.StartsWith("Styles/", StringComparison.Ordinal)
                     || f.StartsWith("Views/", StringComparison.Ordinal) || f.StartsWith("Controls/", StringComparison.Ordinal)))
        {
            foreach (var element in AppSourceTree.LoadXaml(file).Descendants())
            {
                var key = (string?)element.Attribute(AppSourceTree.Xaml + "Key");
                if (key is null)
                {
                    continue;
                }

                if (element.Name.LocalName == "ControlTemplate" && SwapsPresenterForeground(element))
                {
                    templates.Add(key);
                }
                else if (element.Name.LocalName == "Style")
                {
                    styles[key] = element;
                }
            }
        }

        bool IsHost(string key, int depth)
        {
            if (depth > 16 || !styles.TryGetValue(key, out var style))
            {
                return false;
            }

            var template = style.Elements().FirstOrDefault(s => (string?)s.Attribute("Property") == "Template");
            if (template is not null)
            {
                return StaticKey((string?)template.Attribute("Value")) is { } referenced
                    ? templates.Contains(referenced)
                    : template.Descendants().Any(e => e.Name.LocalName == "ControlTemplate" && SwapsPresenterForeground(e));
            }

            return StaticKey((string?)style.Attribute("BasedOn")) is { } basedOn && IsHost(basedOn, depth + 1);
        }

        return (styles.Keys.Where(k => IsHost(k, 0)).ToHashSet(StringComparer.Ordinal), templates);
    }

    private static bool SwapsPresenterForeground(XElement template) =>
        template.Descendants().Any(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Target") == "ContentPresenter.Foreground");

    private static string? StaticKey(string? value) =>
        value is not null && value.StartsWith("{StaticResource ", StringComparison.Ordinal) && value.EndsWith('}')
            ? value["{StaticResource ".Length..^1].Trim()
            : null;

    private static XElement IconTemplate() =>
        AppSourceTree.LoadXaml(Primitives).Descendants()
            .Single(e => e.Name.LocalName == "ControlTemplate" && (string?)e.Attribute("TargetType") == "primitives:SaIcon");

    private static string Entry(XElement dictionary, string key) =>
        dictionary.Elements().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == key).Value.Trim();

    [GeneratedRegex(@"\bHighContrastChanged\b")]
    private static partial Regex HighContrastEvent();
}
