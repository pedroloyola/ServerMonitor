namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 LAYOUT-01..03 (Prism UI12A): surfaces that must fit the Standard window's minimum (560x640 physical px, ~373-560
/// DIP of content depending on the scale) never resolve to a fixed width larger than that. Static; the visual check at
/// 560x640 / 100 % and 150 % is Beacon's (R-5).
/// </summary>
public sealed class Ui12LayoutGuardTests
{
    /// <summary>LAYOUT-02: the toast is at most 600 wide, never fixed at 600 (style or consumer).</summary>
    [Fact]
    public void ToastsNeverResolveToAFixedWidth()
    {
        var style = AppSourceTree.LoadXaml("Styles/Components/Sa.Primitives.xaml").Descendants()
            .Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "primitives:SaToast");
        var setters = style.Elements().Where(e => e.Name.LocalName == "Setter").ToDictionary(s => (string)s.Attribute("Property")!, s => (string?)s.Attribute("Value"));
        Assert.False(setters.ContainsKey("Width"), "SaToast style sets a fixed Width");
        Assert.Equal("600", setters["MaxWidth"]);

        var toasts = AppSourceTree.Files(".xaml").Where(f => f.StartsWith("Views/", StringComparison.Ordinal))
            .SelectMany(f => AppSourceTree.LoadXaml(f).Descendants().Where(e => e.Name.LocalName == "SaToast").Select(e => (File: f, Toast: e)))
            .ToList();
        Assert.Equal(3, toasts.Count);
        Assert.All(toasts, t => Assert.Null(t.Toast.Attribute("Width")));
    }
}
