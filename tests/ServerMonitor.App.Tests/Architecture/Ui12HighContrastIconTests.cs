using System.Text.RegularExpressions;
using ServerMonitor.App.Controls.Primitives;
using Ancestor = ServerMonitor.App.Controls.Primitives.SaIcon.IconAncestor;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 B12B-01 (Beacon re-QA, P2, measured in real High Contrast): the checked sidebar item, the selected segments and
/// the icon-only Glass buttons on hover/pressed swap their ContentPresenter's text to HighlightText on the Highlight fill,
/// but the SaIcon never followed (WindowText on Highlight, 1.44-1.78:1). In HC the icon of a button now strokes with that
/// presenter's Foreground; Light/Dark keep the template's own stroke. Rules pure + wiring by source; the HC capture stays
/// Beacon's.
/// </summary>
public sealed partial class Ui12HighContrastIconTests
{
    // Chains nearest first: O = other element, P = ContentPresenter, B = ButtonBase, C = any other Control.
    [Theory]
    [InlineData("O P O B", 1)]      // sidebar / segment: SaIcon > Grid > ContentPresenter > RootGrid > RadioButton
    [InlineData("P O O O B", 0)]    // icon button: SaIcon > ContentPresenter > Grid > Border > Grid > Button
    [InlineData("P O P B", 2)]      // content inside content: the presenter NEAREST the button (its states swap that one)
    [InlineData("P C P B", -1)]     // another control first (a list item, a user control): not a button's icon
    [InlineData("O B", -1)]         // a button without a presenter on the way
    [InlineData("P O O", -1)]       // no button at all (page content)
    [InlineData("", -1)]
    public void TheIconFollowsThePresenterOfItsButton(string chain, int expected) =>
        Assert.Equal(expected, SaIcon.HostPresenterIndex(Parse(chain)));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]   // HC, but not a button's icon: its own Foreground
    [InlineData(false, true, false)]   // Light / Dark: exactly today's stroke (pixel-identical)
    [InlineData(false, false, false)]
    public void OnlyInHighContrast(bool highContrast, bool hasHostPresenter, bool follows) =>
        Assert.Equal(follows, SaIcon.StrokeFollowsHost(highContrast, hasHostPresenter));

    [Fact]
    public void TheIcon_TracksThePresentersForeground_AndTheHighContrastSwitch()
    {
        var icon = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaIcon.cs");

        Assert.Contains("Loaded += OnLoaded;", icon, StringComparison.Ordinal);
        Assert.Contains("SaHighContrastWatch.Changed += OnHighContrastChanged;", icon, StringComparison.Ordinal);
        Assert.Contains("if (StrokeFollowsHost(SaHighContrastWatch.IsOn, presenter is not null))", icon, StringComparison.Ordinal);
        Assert.Contains("RegisterPropertyChangedCallback(ContentPresenter.ForegroundProperty, (_, _) => ApplyHostStroke())", icon, StringComparison.Ordinal);
        Assert.Contains("_path.Stroke = _hostPresenter.Foreground ?? Foreground;", icon, StringComparison.Ordinal);
        Assert.Matches(TheWalkClassifiesWithThePureRule(), icon);
    }

    /// <summary>
    /// Cortex r4 R4-N1: the HC switch is a STATIC event, so a subscription leak would keep every icon alive. Loaded unsubscribes
    /// before subscribing (a re-Loaded icon is subscribed once); Unloaded returns while the icon is still live (a remount's stale
    /// Unloaded after the new Loaded must not drop the new subscription), otherwise unsubscribes, then lets the presenter go.
    /// Source pin: the lifecycle needs a XAML runtime (Beacon: the count of icons answering an HC switch must not grow).
    /// </summary>
    [Fact]
    public void TheIcon_SubscribesOnce_AndUnsubscribesWhenItLeavesTheTree()
    {
        var icon = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaIcon.cs");

        Assert.Matches(LoadedSubscribesOnce(), icon);
        Assert.Matches(UnloadedUnsubscribesUnlessLive(), icon);
        Assert.Single(Regex.Matches(icon, Regex.Escape("SaHighContrastWatch.Changed += ")));
        Assert.Equal(2, Regex.Matches(icon, Regex.Escape("SaHighContrastWatch.Changed -= OnHighContrastChanged;")).Count);
    }

    /// <summary>
    /// The swap the icon follows is the one on the template part named "ContentPresenter": in every template whose states
    /// set <c>ContentPresenter.Foreground</c>, that part must BE a ContentPresenter (a TextBlock part would leave the icon
    /// on its rest colour again).
    /// </summary>
    [Fact]
    public void EveryForegroundSwap_TargetsARealContentPresenter()
    {
        var failures = new List<string>();
        var templates = 0;
        foreach (var file in AppSourceTree.Files(".xaml").Where(f => !f.StartsWith("Qa/", StringComparison.Ordinal)))
        {
            foreach (var template in AppSourceTree.LoadXaml(file).Descendants().Where(e => e.Name.LocalName == "ControlTemplate"))
            {
                var swaps = template.Descendants().Any(e => e.Name.LocalName == "Setter"
                    && (string?)e.Attribute("Target") == "ContentPresenter.Foreground");
                if (!swaps)
                {
                    continue;
                }

                templates++;
                var part = template.Descendants().SingleOrDefault(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "ContentPresenter");
                if (part?.Name.LocalName != "ContentPresenter")
                {
                    failures.Add($"{file}: {(string?)template.Attribute("TargetType")} template swaps ContentPresenter.Foreground on a {part?.Name.LocalName ?? "missing part"}");
                }
            }
        }

        Assert.True(templates >= 6, $"only {templates} templates swap ContentPresenter.Foreground; the guard would be weak");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>The Checked items over the Highlight sliding indicator (sidebar, segments) swap their text: the icon follows that.</summary>
    [Theory]
    [InlineData("Styles/Components/Sa.Navigation.xaml", "SaNavItemStyle")]
    [InlineData("Styles/Components/Sa.Forms.xaml", "SaSegmentedRectItemStyle")]
    [InlineData("Styles/Components/Sa.Forms.xaml", "SaSegmentedPillItemStyle")]
    public void CheckedItemsOverTheSlidingIndicator_SwapThePresenter(string file, string style)
    {
        var checkedState = AppSourceTree.LoadXaml(file).Descendants()
            .Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute(AppSourceTree.Xaml + "Key") == style)
            .Descendants().Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "Checked");
        Assert.Contains(checkedState.Descendants(), s => s.Name.LocalName == "Setter"
            && (string?)s.Attribute("Target") == "ContentPresenter.Foreground"
            && (string?)s.Attribute("Value") == "{ThemeResource SaSelectedTextBrush}");
    }

    private static Ancestor[] Parse(string chain) =>
        chain.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(c => c switch
        {
            "P" => Ancestor.Presenter,
            "B" => Ancestor.Button,
            "C" => Ancestor.OtherControl,
            _ => Ancestor.Other,
        }).ToArray();

    [GeneratedRegex(@"private void OnLoaded\(object sender, RoutedEventArgs e\)\s*\{\s*SaHighContrastWatch\.Changed -= OnHighContrastChanged;\s*SaHighContrastWatch\.Changed \+= OnHighContrastChanged;\s*UpdateStrokeSource\(\);\s*\}")]
    private static partial Regex LoadedSubscribesOnce();

    [GeneratedRegex(@"private void OnUnloaded\(object sender, RoutedEventArgs e\)\s*\{\s*if \(SaLiveTree\.IsLive\(this\)\)\s*\{\s*return;\s*\}\s*SaHighContrastWatch\.Changed -= OnHighContrastChanged;\s*DetachHost\(\);\s*\}")]
    private static partial Regex UnloadedUnsubscribesUnlessLive();

    [GeneratedRegex(@"ContentPresenter => IconAncestor\.Presenter,\s*ButtonBase => IconAncestor\.Button,\s*Control => IconAncestor\.OtherControl,[\s\S]*?var index = HostPresenterIndex\(chain\);")]
    private static partial Regex TheWalkClassifiesWithThePureRule();
}
