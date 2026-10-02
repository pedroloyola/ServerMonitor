using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// T-17 (UI.2). Accessible names for the component layer: colour or an icon is never the only signal.
/// </summary>
public sealed class InteractivePrimitivesAutomationTests
{
    private const string PrimitivesNamespace = "using:ServerMonitor.App.Controls.Primitives";

    /// <summary>Cortex F-2: every XAML use of the status primitive sets a non-empty Label (its automation name).</summary>
    [Fact]
    public void EveryStatusIndicatorUseSetsALabel()
    {
        var failures = new List<string>();
        var uses = 0;
        foreach (var file in AppSourceTree.Files(".xaml"))
        {
            foreach (var element in AppSourceTree.LoadXaml(file).Descendants()
                         .Where(e => e.Name.LocalName == "SaStatusIndicator" && e.Name.NamespaceName == PrimitivesNamespace))
            {
                uses++;
                if (string.IsNullOrWhiteSpace((string?)element.Attribute("Label")))
                {
                    failures.Add($"{file}: <SaStatusIndicator> without a Label");
                }
            }
        }

        Assert.True(uses > 0, "No SaStatusIndicator use found; the guard would be vacuous.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Icon-only buttons and the password eye have no text, so every use names them explicitly.</summary>
    [Fact]
    public void IconOnlyControlsAreAlwaysNamed()
    {
        var failures = new List<string>();
        var uses = 0;
        foreach (var file in AppSourceTree.Files(".xaml"))
        {
            foreach (var element in AppSourceTree.LoadXaml(file).Descendants())
            {
                var style = (string?)element.Attribute("Style") ?? string.Empty;
                // A template part (PART_*) is named in code by its control from a required DP (e.g. SaToast.CloseButtonAutomationName).
                var isTemplatePart = ((string?)element.Attribute(AppSourceTree.Xaml + "Name"))?.StartsWith("PART_", StringComparison.Ordinal) == true;
                if (!isTemplatePart && (style.Contains("SaIconButtonStyle", StringComparison.Ordinal) || style.Contains("SaGhostIconButtonStyle", StringComparison.Ordinal)))
                {
                    uses++;
                    if (string.IsNullOrWhiteSpace(AutomationName(element)))
                    {
                        failures.Add($"{file}: icon-only button without AutomationProperties.Name");
                    }
                }

                if (element.Name.LocalName == "SaToast" && element.Name.NamespaceName == PrimitivesNamespace)
                {
                    uses++;
                    if (string.IsNullOrWhiteSpace((string?)element.Attribute("CloseButtonAutomationName")))
                    {
                        failures.Add($"{file}: SaToast without CloseButtonAutomationName");
                    }
                }

                if (element.Name.LocalName == "SaPasswordField" && element.Name.NamespaceName == PrimitivesNamespace)
                {
                    uses++;
                    if (string.IsNullOrWhiteSpace((string?)element.Attribute("RevealButtonAutomationName")))
                    {
                        failures.Add($"{file}: SaPasswordField without RevealButtonAutomationName");
                    }
                }
            }
        }

        Assert.True(uses > 0, "No icon-only control use found; the guard would be vacuous.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Every interactive sample in the gallery has an accessible name: AutomationProperties.Name, plain-text Content, a
    /// Header, or - for an input - the Header of the SaFormField that labels it (LabeledBy).
    /// </summary>
    [Fact]
    public void EveryInteractiveGallerySampleHasAnAccessibleName()
    {
        string[] interactive =
        [
            "Button", "ToggleButton", "ToggleSwitch", "CheckBox", "RadioButton", "TextBox", "PasswordBox", "ComboBox",
            "SaPasswordField", "SaListRow", "SaBreadcrumb", "ListView", "HyperlinkButton"
        ];
        var failures = new List<string>();
        var samples = 0;
        foreach (var file in AppSourceTree.Files(".xaml").Where(f => f.StartsWith("Qa/Gallery/", StringComparison.Ordinal)))
        {
            foreach (var element in AppSourceTree.LoadXaml(file).Descendants().Where(e => interactive.Contains(e.Name.LocalName)))
            {
                samples++;
                var labelledByField = element.Parent is { } parent && parent.Name.LocalName == "SaFormField"
                    && !string.IsNullOrWhiteSpace((string?)parent.Attribute("Header"));
                if (string.IsNullOrWhiteSpace(AutomationName(element))
                    && string.IsNullOrWhiteSpace((string?)element.Attribute("Content"))
                    && string.IsNullOrWhiteSpace((string?)element.Attribute("Header"))
                    // SaListRow names itself from Title; SaBreadcrumb's parent button from ParentText.
                    && string.IsNullOrWhiteSpace((string?)element.Attribute("Title"))
                    && string.IsNullOrWhiteSpace((string?)element.Attribute("ParentText"))
                    && !labelledByField)
                {
                    failures.Add($"{file}: <{element.Name.LocalName}> without an accessible name");
                }
            }
        }

        Assert.True(samples > 20, $"Only {samples} interactive gallery samples found; the guard would be (nearly) vacuous.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>A form error is announced: PART_Error of SaFormField is an assertive live region.</summary>
    [Fact]
    public void FormFieldErrorIsAnAssertiveLiveRegion()
    {
        var error = AppSourceTree.LoadXaml("Styles/Components/Sa.Primitives.xaml").Descendants()
            .Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "PART_Error");
        Assert.Equal("Assertive", (string?)error.Attribute("AutomationProperties.LiveSetting"));
    }

    /// <summary>
    /// The literal AutomationProperties.Name, or - for a localized element - its x:Uid, but only when every culture's
    /// resw defines a non-empty "{uid}.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name" (UI.3 pages).
    /// </summary>
    private static string? AutomationName(XElement element)
    {
        var literal = (string?)element.Attribute("AutomationProperties.Name");
        if (!string.IsNullOrWhiteSpace(literal))
        {
            return literal;
        }

        var uid = (string?)element.Attribute(AppSourceTree.Xaml + "Uid");
        if (string.IsNullOrWhiteSpace(uid))
        {
            return null;
        }

        var key = uid + ".[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";
        foreach (var culture in new[] { "pt-PT", "pt-BR", "en-US" })
        {
            var resw = XDocument.Load(Path.Combine(AppSourceTree.RepositoryRoot, "src", "ServerMonitor.App", "Resources", culture, "Resources.resw"));
            var value = resw.Root!.Elements("data").FirstOrDefault(d => (string?)d.Attribute("name") == key)?.Element("value")?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
        }

        return uid;
    }
}
