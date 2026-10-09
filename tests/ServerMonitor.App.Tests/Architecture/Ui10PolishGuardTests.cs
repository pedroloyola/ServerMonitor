using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.10 (GLOBAL VISUAL POLISH) guards: the rules the polish centralised must not silently regress. Each rule names its
/// finding (Prism UI.10A audit) and the Figma node that now draws it (UI.10B).
/// </summary>
public sealed partial class Ui10PolishGuardTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = AppSourceTree.Xaml;

    /// <summary>The dictionaries that define Button styles in the component layer.</summary>
    private static readonly string[] ButtonStyleDictionaries =
    [
        "Styles/Components/Sa.Buttons.xaml", "Styles/Components/Sa.Dialogs.xaml", "Styles/Components/Sa.Primitives.xaml"
    ];

    /// <summary>
    /// F06 justified exceptions (audit §1 H03 rule 4; Figma Manual A1 234:182): they keep a non-pill shape on purpose.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> RadiusExceptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SaButtonBaseStyle"] = "the shared base: no height, no shape of its own",
        ["SaPickerButtonStyle"] = "the key file picker is a field: r12 like the inputs",
        ["SaPickerButtonErrorStyle"] = "the key file picker in error (a field)",
        ["SaDisclosureButtonStyle"] = "a settings row is a surface (r20), not an action button",
        ["SaCompactRowButtonStyle"] = "the compact server row is a surface (r15)",
        ["SaBreadcrumbParentButtonStyle"] = "a breadcrumb is a text link",
    };

    /// <summary>
    /// F06 (H03): every Button style outside the justified exceptions is a pill - its CornerRadius is exactly the
    /// SaRadiusButton&lt;h&gt; tier token of its effective Height, and that token is h/2. No literal radius on a button.
    /// </summary>
    [Fact]
    public void EveryTextButtonStyle_UsesTheRadiusTierTokenOfItsHeight()
    {
        var tiers = RadiusTiers();
        var styles = ButtonStyles();
        var failures = new List<string>();
        foreach (var (key, _) in styles)
        {
            if (RadiusExceptions.ContainsKey(key))
                continue;
            var height = Effective(styles, key, "Height");
            var radius = Effective(styles, key, "CornerRadius");
            if (height is null || radius is null)
            {
                failures.Add($"{key}: no effective Height/CornerRadius (height={height ?? "-"}, radius={radius ?? "-"})");
                continue;
            }

            var expected = $"{{StaticResource SaRadiusButton{height}}}";
            if (!string.Equals(radius, expected, StringComparison.Ordinal))
                failures.Add($"{key}: h{height} has CornerRadius \"{radius}\", expected \"{expected}\"");
            else if (!tiers.TryGetValue("SaRadiusButton" + height, out var value)
                     || value != double.Parse(height, CultureInfo.InvariantCulture) / 2)
                failures.Add($"{key}: tier SaRadiusButton{height} is missing or not h/2");
        }

        Assert.True(failures.Count == 0, "F06 pill rule broken:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>F06: each tier token is exactly h/2 (a radius above h/2 renders elliptical in WinUI).</summary>
    [Fact]
    public void EveryRadiusTier_IsHalfItsHeight()
    {
        var tiers = RadiusTiers();
        Assert.NotEmpty(tiers);
        Assert.All(tiers, tier => Assert.Equal(double.Parse(tier.Key["SaRadiusButton".Length..], CultureInfo.InvariantCulture) / 2, tier.Value));
    }

    /// <summary>
    /// F06: a page never re-shapes a tiered button locally - no Height or CornerRadius attribute on a Button whose style
    /// carries a tier (a local Height would leave the tier's radius above or below h/2).
    /// </summary>
    [Fact]
    public void NoPageOverridesTheHeightOrRadiusOfATieredButton()
    {
        var styles = ButtonStyles();
        var tiered = styles.Keys.Where(key => !RadiusExceptions.ContainsKey(key)).ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (var file in AppSourceTree.Files(".xaml").Where(file => !AppSourceTree.IsUnderStyles(file)))
        {
            foreach (var button in AppSourceTree.LoadXaml(file).Descendants(Ns + "Button"))
            {
                var style = StyleKey((string?)button.Attribute("Style"));
                if (style is null || !tiered.Contains(style))
                    continue;
                foreach (var property in new[] { "Height", "CornerRadius" })
                    if (button.Attribute(property) is { } attribute)
                        failures.Add($"{file}: Button ({style}) sets {property}=\"{attribute.Value}\"");
            }
        }

        Assert.True(failures.Count == 0, "Local re-shape of a tiered button:\n  " + string.Join("\n  ", failures));
    }

    private static Dictionary<string, double> RadiusTiers() =>
        AppSourceTree.LoadXaml("Styles/Tokens/Radius.xaml").Root!.Elements(Ns + "CornerRadius")
            .Where(element => ((string?)element.Attribute(X + "Key"))?.StartsWith("SaRadiusButton", StringComparison.Ordinal) == true)
            .ToDictionary(element => (string)element.Attribute(X + "Key")!, element => double.Parse(element.Value.Trim(), CultureInfo.InvariantCulture));

    private static Dictionary<string, XElement> ButtonStyles() =>
        ButtonStyleDictionaries
            .SelectMany(file => AppSourceTree.LoadXaml(file).Root!.Elements(Ns + "Style"))
            .Where(style => (string?)style.Attribute("TargetType") == "Button")
            .ToDictionary(style => (string)style.Attribute(X + "Key")!, style => style, StringComparer.Ordinal);

    private static string? Effective(IReadOnlyDictionary<string, XElement> styles, string key, string property)
    {
        for (var current = key; current is not null && styles.TryGetValue(current, out var style); current = StyleKey((string?)style.Attribute("BasedOn")))
        {
            var setter = style.Elements(Ns + "Setter").FirstOrDefault(element => (string?)element.Attribute("Property") == property);
            if (setter is not null)
                return (string?)setter.Attribute("Value");
        }

        return null;
    }

    private static string? StyleKey(string? reference) =>
        reference is null ? null : StaticResourceKey().Match(reference) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"^\{StaticResource\s+(\w+)\}$")]
    private static partial Regex StaticResourceKey();
}
