using System.Xml.Linq;
using Microsoft.UI.Xaml.Input;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services.Motion;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.11.1 H02 opt-in inventory (Cortex C5/B4, Prism §5, Boss S0b verdict V2). Drag-to-select exists on exactly the five
/// segmented selectors; the sidebar and the toggles never get it. The interaction itself is proven behaviourally by
/// SegmentDragControllerTests; these checks complement it (they never prove the routed-event wiring - UI.11.1D runtime QA).
/// </summary>
public sealed class Ui111DragContractTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Primitives = "using:ServerMonitor.App.Controls.Primitives";
    private static readonly XName IndicatorBrush = Primitives + "SaSlidingSelection.IndicatorBrush";
    private static readonly XName AllowDrag = Primitives + "SaSlidingSelection.AllowDrag";

    private static readonly string[] SegmentedHostFiles =
        ["Views/SettingsPage.xaml", "Views/HistoryPage.xaml", "Views/WorkloadsPage.xaml", "Controls/ServerFormControl.xaml"];

    [Fact]
    public void ExactlyTheFiveSegmentedSelectors_AllowDrag()
    {
        var production = AppSourceTree.Files(".xaml").Where(f => !AppSourceTree.IsUnderStyles(f) && !f.StartsWith("Qa/", StringComparison.Ordinal));
        var opted = production
            .SelectMany(file => AppSourceTree.LoadXaml(file).Descendants().Where(e => e.Attribute(AllowDrag) is not null).Select(host => (file, host)))
            .ToList();

        Assert.Equal(5, opted.Count);
        Assert.All(opted, entry =>
        {
            Assert.Contains(entry.file, SegmentedHostFiles);
            Assert.Equal("True", (string?)entry.host.Attribute(AllowDrag));
            Assert.NotNull(entry.host.Attribute(IndicatorBrush)); // on the indicator host itself
            Assert.Equal(MotionTokens.SegmentSelectDuration, (string?)entry.host.Attribute(Primitives + "SaSlidingSelection.DurationKey"));
        });
        Assert.Equal(2, opted.Count(entry => entry.file == "Controls/ServerFormControl.xaml"));
    }

    /// <summary>The sidebar: vertical, Click-driven navigation (B4) - never opted in, and its items keep the default manipulation.</summary>
    [Fact]
    public void TheSidebar_NeverAllowsDrag()
    {
        var sidebar = AppSourceTree.LoadXaml("Controls/SaSidebar.xaml");

        Assert.DoesNotContain(sidebar.Descendants(), e => e.Attribute(AllowDrag) is not null);
        Assert.DoesNotContain(sidebar.Descendants(Presentation + "RadioButton"), item => item.Attribute("ManipulationMode") is not null);
    }

    /// <summary>The toggles are not sliding selections: no style or template anywhere opts into the segmented drag.</summary>
    [Fact]
    public void NoStyle_AndNoToggle_AllowsDrag()
    {
        foreach (var file in AppSourceTree.Files(".xaml").Where(AppSourceTree.IsUnderStyles))
        {
            var document = AppSourceTree.LoadXaml(file);
            Assert.DoesNotContain(document.Descendants(), e => e.Attribute(AllowDrag) is not null
                || e.Elements(Presentation + "Setter").Any(s => ((string?)s.Attribute("Property") ?? "").Contains("AllowDrag", StringComparison.Ordinal)));
        }

        Assert.All(AppSourceTree.Files(".xaml"), file =>
            Assert.DoesNotContain(AppSourceTree.LoadXaml(file).Descendants(Presentation + "ToggleSwitch"), toggle => toggle.Attribute(AllowDrag) is not null));
    }

    /// <summary>Beacon S0b V2: the opted-in items' manipulation keeps System (page pan) plus TranslateX (horizontal drag).</summary>
    [Fact]
    public void TheOptedInItems_UseSystemPlusTranslateX()
    {
        Assert.Equal(ManipulationModes.System | ManipulationModes.TranslateX, SegmentDragWiring.ItemManipulation);
        Assert.True(SegmentDragWiring.ItemManipulation.HasFlag(ManipulationModes.System));
        Assert.False(SegmentDragWiring.ItemManipulation.HasFlag(ManipulationModes.TranslateY));
    }

    /// <summary>
    /// Cortex review C-1: the predicted label switches an item to its Checked visual state while dragging. If that changes the
    /// item's width (Normal -> Medium text on an auto-width item), the item re-measures and SizeChanged cancels the drag. Every
    /// AllowDrag host (production AND gallery) must have items whose width cannot follow the label: the Checked state keeps
    /// the effective FontWeight, or the width is fixed, or the item stretches in a star grid column.
    /// </summary>
    [Fact]
    public void EveryAllowDragHost_HasWidthStableItems()
    {
        var styles = AppSourceTree.LoadXaml("Styles/Components/Sa.Forms.xaml").Descendants(Presentation + "Style")
            .Where(s => s.Attribute(AppSourceTree.Xaml + "Key") is not null)
            .ToDictionary(s => (string)s.Attribute(AppSourceTree.Xaml + "Key")!, StringComparer.Ordinal);

        static string KeyOf(string reference) => reference.Replace("{StaticResource ", "", StringComparison.Ordinal).TrimEnd('}');

        // The first non-null answer up the BasedOn chain.
        string? Up(string key, Func<XElement, string?> read)
        {
            for (XElement? style = styles[key]; style is not null; style = (string?)style.Attribute("BasedOn") is { } basedOn ? styles[KeyOf(basedOn)] : null)
            {
                if (read(style) is { } value)
                {
                    return value;
                }
            }

            return null;
        }

        string? Setter(string key, string property) =>
            Up(key, style => (string?)style.Elements(Presentation + "Setter").FirstOrDefault(s => (string?)s.Attribute("Property") == property)?.Attribute("Value"));

        string? CheckedWeight(string key) =>
            Up(key, style => (string?)style.Descendants(Presentation + "VisualState")
                .FirstOrDefault(s => (string?)s.Attribute(AppSourceTree.Xaml + "Name") == "Checked")?
                .Descendants(Presentation + "Setter").FirstOrDefault(s => (string?)s.Attribute("Target") == "ContentPresenter.FontWeight")?
                .Attribute("Value"));

        var hosts = 0;
        foreach (var file in AppSourceTree.Files(".xaml").Where(f => !AppSourceTree.IsUnderStyles(f)))
        {
            foreach (var host in AppSourceTree.LoadXaml(file).Descendants().Where(e => (string?)e.Attribute(AllowDrag) == "True"))
            {
                hosts++;
                var cell = host.Parent!;
                var items = cell.Elements(Presentation + "RadioButton")
                    .Concat(cell.Elements(Presentation + "StackPanel").SelectMany(p => p.Elements(Presentation + "RadioButton")))
                    .ToList();
                Assert.True(items.Count >= 2, $"{file}: an AllowDrag host without its items");
                foreach (var item in items)
                {
                    var key = KeyOf((string)item.Attribute("Style")!);
                    var weight = (string?)item.Attribute("FontWeight") ?? Setter(key, "FontWeight") ?? "Normal";
                    var sameWeight = (CheckedWeight(key) ?? weight) == weight;
                    var fixedWidth = item.Attribute("Width") is not null || Setter(key, "Width") is not null;
                    var stretch = ((string?)item.Attribute("HorizontalAlignment") ?? Setter(key, "HorizontalAlignment")) == "Stretch";
                    var column = int.Parse((string?)item.Attribute("Grid.Column") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                    var starColumn = cell.Name == Presentation + "Grid"
                        && cell.Elements(Presentation + "Grid.ColumnDefinitions").Elements().ElementAtOrDefault(column) is { } definition
                        && (string?)definition.Attribute("Width") == "*";
                    Assert.True(sameWeight || fixedWidth || (stretch && starColumn),
                        $"{file}: an AllowDrag item ({key}) whose width can follow the predicted label (Checked {CheckedWeight(key)} vs {weight})");
                }
            }
        }

        Assert.Equal(7, hosts); // the five production selectors + the two width-stable gallery samples (Filter variant, Pill)
    }

    /// <summary>
    /// The opt-in defaults to false (C5). A guard over the registration's source text - the DependencyProperty needs the WinUI
    /// runtime to be read in a unit test - complementing the five-host inventory above.
    /// </summary>
    [Fact]
    public void AllowDrag_DefaultsToFalse_AndIsOnlyAppliedWhenOptedIn()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaSlidingSelection.cs");

        Assert.Contains("\"AllowDrag\", typeof(bool), typeof(SaSlidingSelection), new PropertyMetadata(false))", code, StringComparison.Ordinal);
        Assert.Contains("if (_drag is null && SaSlidingSelection.GetAllowDrag(_host)", code, StringComparison.Ordinal);
        // V2 reaches the items only through the adapter (SegmentDragWiring.PrepareItems - behaviourally tested), which exists
        // only for an opted-in track: the opt-in is read in exactly one place.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, @"GetAllowDrag\(_host\)"));
    }

    /// <summary>
    /// The window wiring (complements ThemeRemountFocusTests / the hub tests, which prove the behaviour): a theme change
    /// remounts every root through ONE focus batch with no per-remount restore, and the window publishes its activation.
    /// </summary>
    [Fact]
    public void MainWindow_RemountsThroughTheFocusBatch_AndPublishesItsActivation()
    {
        var code = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, @"_themeRemountFocus\.RemountAll\("));
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(code, @"SaThemeRefresh\.Remount\([^;\n]*?restoreFocus: false\)").Count);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(code, @"SaThemeRefresh\.Remount\(").Count);
        Assert.Contains("WindowActivationHub.Default.Publish(AppWindow.Id, _isWindowActive);", code, StringComparison.Ordinal);
    }
}
