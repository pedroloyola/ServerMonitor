using Microsoft.UI.Xaml;
using ServerMonitor.App.Controls.Primitives;

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

    /// <summary>LAYOUT-01: the dialog surface never asks for more than the window; commands stack when they cannot sit
    /// side by side. Padding 28 + border 1 (SaDialogStyle, Dark/Light).</summary>
    [Theory]
    [InlineData(1280, 800, 2, 620, 720, 768, false)]   // roomy window: the approved 620..720 surface, side by side
    [InlineData(1280, 800, 3, 620, 720, 768, true)]    // R2 N3 (Cortex): min and max surfaces disagree (562 < 612 <= 662) - the NARROWEST decides
    [InlineData(373, 627, 2, 341, 341, 595, true)]     // 560x640 physical at 150 %: clamped, commands stacked
    [InlineData(560, 600, 2, 528, 528, 568, false)]    // 560 at 100 %: clamped, two commands still fit (470 >= 404)
    [InlineData(560, 600, 3, 528, 528, 568, true)]     // ... three do not (612 needed)
    [InlineData(373, 627, 1, 341, 341, 595, false)]    // a single command never "stacks"
    [InlineData(20, 20, 2, 0, 0, 0, true)]             // degenerate: never negative
    public void DialogSurfaceIsClampedToTheWindow(double width, double height, int commands, double minWidth, double maxWidth, double maxHeight, bool stacked)
    {
        var fit = SaDialog.FitToWindow(width, height, new Thickness(28), new Thickness(1), commands);
        Assert.Equal((minWidth, maxWidth, maxHeight, stacked), (fit.MinWidth, fit.MaxWidth, fit.MaxHeight, fit.StackCommands));
    }

    [Fact]
    public void EverySaDialogFitsTheWindowAndScrolls()
    {
        var dialogs = AppSourceTree.LoadXaml("Styles/Components/Sa.Dialogs.xaml");
        var style = dialogs.Descendants().Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "SaDialogStyle");
        Assert.Contains(style.Elements(), s => (string?)s.Attribute("Property") == "primitives:SaDialog.FitsWindow" && (string?)s.Attribute("Value") == "True");
        var scroll = style.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "ContentScrollViewer");
        Assert.Equal("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
        Assert.Single(style.Descendants(), e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "CommandStack");
        Assert.Single(style.Descendants(), e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "BackgroundElement");
    }

    /// <summary>FORM-01: the subject of a security / destructive decision is never cut to one line (host-key trust, the
    /// connection test endpoint, "what will be removed"): it wraps, up to 3 lines.</summary>
    [Theory]
    [InlineData("Views/ServerEditorTrustPanel.xaml", "SubjectText")]
    [InlineData("Views/ServerEditorTestPanel.xaml", "SubjectText")]
    [InlineData("Views/DestructiveConfirmDialog.xaml", null)]
    public void DecisionSubjectsWrapInsteadOfTrimming(string file, string? name)
    {
        var document = AppSourceTree.LoadXaml(file);
        var text = name is not null
            ? document.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == name)
            : document.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "AffectedRow")
                .Descendants().Single(e => e.Name.LocalName == "TextBlock");
        Assert.Equal("Wrap", (string?)text.Attribute("TextWrapping"));
        Assert.Equal("3", (string?)text.Attribute("MaxLines"));
        Assert.NotEqual("StackPanel", text.Parent!.Name.LocalName); // a horizontal StackPanel never lets text wrap
    }
}
