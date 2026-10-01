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
}
