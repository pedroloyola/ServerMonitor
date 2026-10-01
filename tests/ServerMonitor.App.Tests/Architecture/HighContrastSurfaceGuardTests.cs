using System.Xml.Linq;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// F-07 (Boss B-5): in High Contrast a modal or dialog keeps an OPAQUE smoke behind it and draws a 2px WindowText
/// border (Microsoft guidance for transient surfaces); 1px in Dark/Light. Static: real HC QA stays NOT_RUN (no agent
/// switches the system contrast theme).
/// </summary>
public sealed class HighContrastSurfaceGuardTests
{
    [Fact]
    public void ModalBorderIsTwoPixelsOnlyInHighContrast()
    {
        var themes = AppSourceTree.LoadXaml("Styles/Components/Sa.Surfaces.xaml").Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries").Elements()
            .ToDictionary(e => (string)e.Attribute(AppSourceTree.Xaml + "Key")!, Thickness, StringComparer.Ordinal);

        Assert.Equal("1", themes["Dark"]);
        Assert.Equal("1", themes["Light"]);
        Assert.Equal("2", themes["HighContrast"]);

        static string Thickness(XElement theme) => theme.Elements()
            .Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "SaModalBorderThickness").Value.Trim();
    }

    [Theory]
    [InlineData("Styles/Components/Sa.Surfaces.xaml", "SaModalSurfaceStyle")]
    [InlineData("Styles/Components/Sa.Dialogs.xaml", "SaDialogStyle")]
    public void ModalShellsUseTheThemedBorderAndTheGlassContour(string file, string style)
    {
        var setters = AppSourceTree.LoadXaml(file).Descendants()
            .Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute(AppSourceTree.Xaml + "Key") == style)
            .Elements().Where(e => e.Name.LocalName == "Setter")
            .ToDictionary(e => (string)e.Attribute("Property")!, e => (string)e.Attribute("Value")!, StringComparer.Ordinal);

        Assert.Equal("{ThemeResource SaModalBorderThickness}", setters["BorderThickness"]);
        Assert.Equal("{ThemeResource SaGlassBorderBrush}", setters["BorderBrush"]);
    }

    [Fact]
    public void HighContrastSmokeAndContourAreOpaqueSystemColours()
    {
        var hc = (string file) => AppSourceTree.LoadXaml(file).Descendants()
            .Where(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "HighContrast")
            .Elements().ToDictionary(e => (string)e.Attribute(AppSourceTree.Xaml + "Key")!, e => (string?)e.Attribute("Color"), StringComparer.Ordinal);

        Assert.Equal("{ThemeResource SystemColorWindowColor}", hc("Styles/Tokens/Elevation.xaml")["SaOverlaySmokeBrush"]);
        Assert.Equal("{ThemeResource SystemColorWindowTextColor}", hc("Styles/Tokens/Color.Semantic.xaml")["SaGlassBorderBrush"]);
    }
}
