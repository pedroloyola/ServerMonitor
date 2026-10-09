using System.Xml.Linq;
using ServerMonitor.App.Services.Motion;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.11 F05-F17 static guards (Prism UI11A §3/§4, Boss scope). Each pattern is where the audit put it and nowhere it
/// would lie: hover-fade only on hover/pressed overlays (never a selection Shell), the page entrance has no lateral
/// offset and obeys Reduced Motion, the dialog and the toggle share the template gate, transients rise in, content
/// fades in only after loading/empty/error (never on a filter result), the indeterminate bars run only while busy.
/// </summary>
public sealed class Ui11FindingsGuardTests
{
    private static readonly XNamespace Primitives = "using:ServerMonitor.App.Controls.Primitives";
    private static readonly XName HoverFade = Primitives + "SaMotion.HoverFade";
    private static readonly XName Enter = Primitives + "SaMotion.Enter";

    private static IEnumerable<(string File, XElement Element)> AllElements() =>
        AppSourceTree.Files(".xaml").SelectMany(file => AppSourceTree.LoadXaml(file).Descendants().Select(e => (file, e)));

    private static string? Name(XElement element) => (string?)element.Attribute(AppSourceTree.Xaml + "Name");

    [Fact]
    public void F05_HoverFade_OnlyOnHoverAndPressedOverlays_NeverOnASelectionShell()
    {
        var faded = AllElements().Where(x => (string?)x.Element.Attribute(HoverFade) == "True").ToList();

        Assert.True(faded.Count >= 15, $"expected the Sa overlays to fade (found {faded.Count})");
        foreach (var (file, element) in faded)
        {
            Assert.Contains(Name(element), new[] { "StateOverlay", "HoverOverlay", "SwitchAreaGrid" });
            // A BrushTransition cross-fades only from a real brush: the resting value is an explicit Transparent.
            Assert.Equal("Transparent", (string?)element.Attribute("Background"));
            Assert.Null(element.Element(element.Name + ".RenderTransform"));
            Assert.False(file.EndsWith("Sa.Forms.xaml", StringComparison.Ordinal) && Name(element) == "Shell", file);
        }

        Assert.DoesNotContain(AllElements(), x => Name(x.Element) == "Shell" && x.Element.Attribute(HoverFade) is not null);
        Assert.DoesNotContain(AllElements(), x => x.Element.Name.LocalName == "BrushTransition");
    }

    [Fact]
    public void F06_PageEntrance_IsNeutral_ArmedAfterLaunch_AndClearedByReducedMotion()
    {
        var code = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");

        Assert.Contains("FromHorizontalOffset = 0", code, StringComparison.Ordinal);
        Assert.Contains("FromVerticalOffset = PageEntranceRise", code, StringComparison.Ordinal);
        Assert.Contains("internal const double PageEntranceRise = 12;", code, StringComparison.Ordinal);
        Assert.Contains("ContentFrame.ContentTransitions = Services.Motion.MotionPolicy.IsReduced", code, StringComparison.Ordinal);
        Assert.Contains("Services.Motion.MotionPolicy.Source.Changed -= OnReducedMotionChanged;", code, StringComparison.Ordinal);
        // Armed from the root's Loaded (after the startup page), never in the constructor.
        var loaded = code.IndexOf("private async void OnRootLayoutLoaded(", StringComparison.Ordinal);
        Assert.True(code.IndexOf("ApplyPageEntrance();", loaded, StringComparison.Ordinal) > loaded);
        Assert.Equal(-1, code.IndexOf("ContentTransitions", 0, code.IndexOf("private void ConfigureWindow()", StringComparison.Ordinal), StringComparison.Ordinal));
        // Navigation itself is untouched: no Frame.Navigate (PH-3).
        Assert.DoesNotContain(".Navigate(typeof", AppSourceTree.CodeWithoutComments("Services/NavigationService.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void F07_TheSidebar_HasOneVerticalIndicator_AndNavItemsNoLongerPaintTheirFill()
    {
        var sidebar = AppSourceTree.LoadXaml("Controls/SaSidebar.xaml").Descendants().Single(e => Name(e) == "NavGrid");
        var host = sidebar.Elements().First(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal));

        Assert.Equal("{ThemeResource SaSelectedBrush}", (string?)host.Attribute(Primitives + "SaSlidingSelection.IndicatorBrush"));
        Assert.Equal("6", (string?)host.Attribute("Grid.RowSpan"));
        var nav = AppSourceTree.LoadXaml("Styles/Components/Sa.Navigation.xaml").Descendants()
            .Where(e => e.Name.LocalName == "Setter").Select(e => (string?)e.Attribute("Target") ?? "");
        Assert.DoesNotContain(nav, target => target.StartsWith("Shell.", StringComparison.Ordinal) || target.StartsWith("Highlight.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Styles/Components/Sa.Dialogs.xaml")]
    [InlineData("Styles/Components/Sa.Forms.xaml")]
    public void F09_TemplateMotion_IsGatedByReducedMotion(string file)
    {
        Assert.Contains(AppSourceTree.LoadXaml(file).Descendants(), e =>
            e.Name.LocalName == "MotionVisualStateManager" && e.Name.NamespaceName == "using:ServerMonitor.App.Services.Motion");
    }

    [Fact]
    public void F08_F14_TheEditorModalLayer_UsesTheDialogPattern()
    {
        var layer = AppSourceTree.LoadXaml("Views/ServerEditorPage.xaml").Descendants().Single(e => Name(e) == "DialogLayer");
        var code = AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs");

        Assert.Equal("True", (string?)layer.Attribute(Primitives + "SaMotion.ExitFade"));
        Assert.Contains("SaMotion.PlayEnter(DialogLayer, SaMotionEnter.Fade);", code, StringComparison.Ordinal);
        Assert.Contains("SaMotion.PlayEnter(DialogSurface, SaMotionEnter.Dialog);", code, StringComparison.Ordinal);
        Assert.Contains("SaMotion.PlayEnter(step, SaMotionEnter.Fade);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void F10_Transients_RiseIn_AndTheToastFadesOut()
    {
        var toast = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaToast.cs");
        var notice = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaInlineNotice.cs");

        Assert.Contains("SaMotion.SetEnter(this, SaMotionEnter.Rise);", toast, StringComparison.Ordinal);
        Assert.Contains("SaMotion.SetExitFade(this, true);", toast, StringComparison.Ordinal);
        Assert.Contains("SaMotion.SetEnter(this, SaMotionEnter.Rise);", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("SetExitFade", notice, StringComparison.Ordinal); // entrance only (Prism F10)
    }

    [Fact]
    public void F11_ContentEnter_FollowsStates_NeverAFilterResultOrTheSkeleton()
    {
        var entering = AllElements().Where(x => (string?)x.Element.Attribute(Enter) == "Fade" && x.File.StartsWith("Views/", StringComparison.Ordinal)).ToList();

        Assert.True(entering.Count >= 15, $"expected the state blocks of the five pages (found {entering.Count})");
        foreach (var (file, element) in entering)
        {
            var visibility = (string?)element.Attribute("Visibility") ?? "";
            Assert.False(visibility.Contains("NoResults", StringComparison.Ordinal), $"{file}: a filter result must stay instant ({visibility})");
            Assert.False(visibility.Contains("Loading", StringComparison.Ordinal), $"{file}: the skeleton itself never fades ({visibility})");
        }
    }

    [Theory]
    [InlineData("Views/BackupCreateDialog.xaml")]
    [InlineData("Views/RestoreOpenDialog.xaml")]
    public void F17_TheIndeterminateBar_RunsOnlyWhileBusy(string file)
    {
        var bar = AppSourceTree.LoadXaml(file).Descendants().Single(e => e.Name.LocalName == "ProgressBar");

        Assert.Equal("{x:Bind Session.IsBusy, Mode=OneWay}", (string?)bar.Attribute("IsIndeterminate"));
    }

    [Fact]
    public void Metrics_StatusAndCharts_StayInstant()
    {
        // Prism §3 "instantâneo correto": no motion attached to the metric, status or chart primitives.
        foreach (var file in new[] { "Controls/HistoryChart.xaml", "Controls/CompactShell.xaml" })
        {
            Assert.DoesNotContain(AppSourceTree.LoadXaml(file).Descendants(), e => e.Attribute(Enter) is not null);
        }

        foreach (var file in new[] { "Controls/Primitives/SaSegmentMeter.cs", "Controls/Primitives/SaCompactMetricBar.cs", "Controls/Primitives/SaPulseBars.cs", "Controls/Primitives/SaStatusIndicator.cs" })
        {
            var code = AppSourceTree.CodeWithoutComments(file);
            Assert.DoesNotContain("SaMotion", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Storyboard", code, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, false, false, false)] // first presentation: not loaded yet
    [InlineData(true, false, true, false, false)] // collapsing never "enters"
    [InlineData(true, true, true, true, false)]   // Reduced Motion
    [InlineData(false, true, true, false, false)] // no pattern
    public void EnterRule(bool hasEnter, bool becameVisible, bool isLoaded, bool reduced, bool plays)
    {
        Assert.Equal(plays, MotionRules.PlaysEnter(hasEnter, becameVisible, isLoaded, reduced));
    }
}
