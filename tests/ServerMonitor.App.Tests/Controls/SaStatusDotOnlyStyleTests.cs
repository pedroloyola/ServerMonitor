using System.Xml.Linq;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.Controls;

/// <summary>
/// UI.3 (Boss decision): the dot-only status is a STYLE of the existing SaStatusIndicator, not a loose Ellipse and not
/// a new control. These read the component XAML (no XAML runtime): the variant must reuse the one template - so the
/// status -> brush / High Contrast mapping exists exactly once - and may change nothing but the label rendering and
/// the UIA view. The runtime counterpart is the --qa-tokens "dot-only" probe.
/// </summary>
public sealed class SaStatusDotOnlyStyleTests
{
    private const string File = "Styles/Components/Sa.Primitives.xaml";

    private static IEnumerable<XElement> StatusStyles() =>
        AppSourceTree.LoadXaml(File).Descendants()
            .Where(e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "primitives:SaStatusIndicator");

    private static XElement Keyed(string key) =>
        Assert.Single(StatusStyles(), s => (string?)s.Attribute(AppSourceTree.Xaml + "Key") == key);

    private static Dictionary<string, string> Setters(XElement style) =>
        style.Elements().Where(e => e.Name.LocalName == "Setter")
            .ToDictionary(s => (string)s.Attribute("Property")!, s => (string?)s.Attribute("Value") ?? "<element>", StringComparer.Ordinal);

    [Fact]
    public void DotOnlyStyleOnlyHidesTheLabelAndIsRawForAutomation()
    {
        var dotOnly = Keyed("SaStatusDotOnlyStyle");

        Assert.Equal("{StaticResource SaStatusIndicatorStyle}", (string?)dotOnly.Attribute("BasedOn"));
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["IsLabelVisible"] = "False",
                ["AutomationProperties.AccessibilityView"] = "Raw"
            },
            Setters(dotOnly));
        Assert.DoesNotContain(dotOnly.Descendants(), e => e.Name.LocalName == "ControlTemplate");
    }

    [Fact]
    public void TheStatusTemplateExistsOnceAndTheImplicitDefaultIsTheBase()
    {
        var templates = AppSourceTree.Files(".xaml")
            .Where(f => f.StartsWith("Styles/", StringComparison.Ordinal))
            .SelectMany(f => AppSourceTree.LoadXaml(f).Descendants())
            .Where(e => e.Name.LocalName == "ControlTemplate" && (string?)e.Attribute("TargetType") == "primitives:SaStatusIndicator");
        Assert.Single(templates);
        Assert.Single(Keyed("SaStatusIndicatorStyle").Descendants(), e => e.Name.LocalName == "ControlTemplate");

        var implicitDefault = Assert.Single(StatusStyles(), s => s.Attribute(AppSourceTree.Xaml + "Key") is null);
        Assert.Equal("{StaticResource SaStatusIndicatorStyle}", (string?)implicitDefault.Attribute("BasedOn"));
        Assert.Empty(implicitDefault.Elements()); // the default keeps the label visible
    }

    [Fact]
    public void LabelHiddenCollapsesOnlyTheLabel()
    {
        var hidden = Assert.Single(Keyed("SaStatusIndicatorStyle").Descendants(),
            e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "LabelHidden");
        var setters = hidden.Descendants().Where(e => e.Name.LocalName == "Setter")
            .Select(s => $"{(string?)s.Attribute("Target")}={(string?)s.Attribute("Value")}")
            .ToArray();

        Assert.Equal(new[] { "PART_Label.Visibility=Collapsed" }, setters);
        var visible = Assert.Single(Keyed("SaStatusIndicatorStyle").Descendants(),
            e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "LabelVisible");
        Assert.Empty(visible.Descendants());
    }
}
