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
        Assert.Equal(
            ["Name", "Stroke", "StrokeEndLineCap", "StrokeLineJoin", "StrokeStartLineCap", "StrokeThickness", "Visibility"],
            rest.Attributes().Select(a => a.Name.LocalName).Order(StringComparer.Ordinal));
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

    private static XElement IconTemplate() =>
        AppSourceTree.LoadXaml(Primitives).Descendants()
            .Single(e => e.Name.LocalName == "ControlTemplate" && (string?)e.Attribute("TargetType") == "primitives:SaIcon");

    private static string Entry(XElement dictionary, string key) =>
        dictionary.Elements().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == key).Value.Trim();

    [GeneratedRegex(@"\bHighContrastChanged\b")]
    private static partial Regex HighContrastEvent();
}
