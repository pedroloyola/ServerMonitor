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

        Assert.True(entering.Count >= 13, $"expected the state blocks of the five pages (found {entering.Count}; P-1 removed the two search-driven ones)");
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

    /// <summary>
    /// UI.11C runtime finding (F07): after a remount whose Loaded precedes the stale Unloaded, IsLoaded can stay false for an
    /// element on screen. The motion behaviours gate on the live tree (SaLiveTree), never on IsLoaded / a Loaded flag.
    /// </summary>
    [Theory]
    [InlineData("Controls/Primitives/SaSlidingSelection.cs")]
    [InlineData("Controls/Primitives/SaMotion.cs")]
    public void MotionBehaviours_GateOnTheLiveTree_NotIsLoaded(string file)
    {
        var code = AppSourceTree.CodeWithoutComments(file);

        Assert.DoesNotContain(".IsLoaded", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_loaded", code, StringComparison.Ordinal);
        Assert.Contains("SaLiveTree.IsLive(", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Prism P-1: a content-enter never follows a state that a search or filter can toggle. Each Fade block's Visibility
    /// property is resolved in its view model (expression-bodied bool properties expanded transitively) and must not depend
    /// on a query, a filter, a "no results" state or the filtered row collection.
    /// </summary>
    [Fact]
    public void F11_ContentEnter_NeverOnAStateThatASearchOrFilterCanToggle()
    {
        var models = AppSourceTree.Files(".cs").Where(f => f.StartsWith("ViewModels/", StringComparison.Ordinal))
            .Select(AppSourceTree.CodeWithoutComments).ToList();
        var checkedBindings = 0;
        foreach (var (file, element) in AllElements().Where(x => (string?)x.Element.Attribute(Enter) == "Fade" && x.File.StartsWith("Views/", StringComparison.Ordinal)))
        {
            var visibility = (string?)element.Attribute("Visibility") ?? "";
            var match = System.Text.RegularExpressions.Regex.Match(visibility, @"\{(?:x:Bind ViewModel\.|Binding )(\w+)");
            Assert.True(match.Success, $"{file}: unrecognised Visibility binding {visibility}");
            var expanded = Expand(match.Groups[1].Value, models, depth: 0);
            checkedBindings++;
            foreach (var token in new[] { "Query", "Filter", "Search", "NoResults", "_rows" })
            {
                Assert.False(expanded.Contains(token, StringComparison.Ordinal), $"{file}: {match.Groups[1].Value} depends on '{token}' -> {expanded}");
            }
        }

        Assert.True(checkedBindings >= 13, $"expected the state blocks (found {checkedBindings})");
    }

    private static string Expand(string property, IReadOnlyList<string> models, int depth)
    {
        foreach (var code in models)
        {
            var m = System.Text.RegularExpressions.Regex.Match(code, @"public bool " + property + @"\s*=>\s*([^;]+);");
            if (m.Success)
            {
                var body = m.Groups[1].Value;
                if (depth >= 3)
                {
                    return body;
                }

                var nested = System.Text.RegularExpressions.Regex.Matches(body, @"\b([A-Z]\w+)\b").Select(x => x.Groups[1].Value).Distinct()
                    .Select(name => Expand(name, models, depth + 1));
                return body + " | " + string.Join(" | ", nested);
            }
        }

        return property; // a stored property (field-backed): its own name only
    }

    /// <summary>Cortex R-1: the deferred dialog scale re-checks the gate and a reset that landed before it.</summary>
    [Fact]
    public void R1_TheDeferredDialogScale_ReChecksTheGateAndStaleness()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaMotion.cs");

        Assert.Contains("if (generation != _generation || MotionPolicy.IsReduced || !SaLiveTree.IsLive(_element))", code, StringComparison.Ordinal);
        Assert.Contains("var generation = ++_generation;", code, StringComparison.Ordinal);
        var reset = code[code.IndexOf("private void ResetVisual()", StringComparison.Ordinal)..];
        Assert.Contains("_generation++;", reset, StringComparison.Ordinal);
    }

    /// <summary>Cortex R-2/R-3: the deferred page-entrance subscription respects a closed window; the Compact switch is a flag.</summary>
    [Fact]
    public void R2_R3_MainWindow_ClosedGuard_AndAnnouncedSwitchFlag()
    {
        var code = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        var loaded = code.IndexOf("private async void OnRootLayoutLoaded(", StringComparison.Ordinal);
        var lambda = code[loaded..code.IndexOf("ApplyPageEntrance();", loaded, StringComparison.Ordinal)];

        Assert.Contains("if (_closed)", lambda, StringComparison.Ordinal);
        Assert.Contains("_closed = true;", code[code.IndexOf("private void OnWindowClosed(", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("var switchedToCompact = mode == WindowMode.Compact && _compactSwitchAnnounced;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("StandardRoot.Opacity == 0", code, StringComparison.Ordinal);
    }

    /// <summary>Cortex R-6: the indicator takes the thread's compositor, never the host's handoff visual.</summary>
    [Fact]
    public void R6_TheIndicator_DoesNotTakeTheHostsHandoffVisual()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaSlidingSelection.cs");

        Assert.DoesNotContain("GetElementVisual(_host)", code, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.GetCompositorForCurrentThread()", code, StringComparison.Ordinal);
    }

    // ---------------- Fix Round 2 (independent tests review T-1..T-3): the runtime WIRING of the pure rules ----------------

    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var open = code.IndexOf('{', start);
        var arrow = code.IndexOf("=>", start, StringComparison.Ordinal);
        if (arrow >= 0 && arrow < open)
        {
            return code[arrow..code.IndexOf(';', arrow)];
        }

        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            depth += code[i] == '{' ? 1 : code[i] == '}' ? -1 : 0;
            if (depth == 0)
            {
                return code[open..(i + 1)];
            }
        }

        throw new InvalidOperationException(signature);
    }

    private static string Squash(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>
    /// T-1: the presenter passes the live Reduced Motion state to the planner (O3), restarts every animation from the
    /// PRESENTED value - the only keyframe at 0 is <c>this.StartingValue</c> (O2) - and each event requests exactly its
    /// cause: Loaded only FirstLayout/Remount, never Selection (O5); a check change Selection; a size change Resize; a scale
    /// change DpiChange; the setting change ReducedMotionChanged.
    /// </summary>
    [Fact]
    public void T1_TheIndicatorPresenter_WiresThePlannerExactly()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaSlidingSelection.cs");

        Assert.Contains("_planner.Flush(rects, selected, MotionPolicy.IsReduced)", code, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, @"_planner\.Flush\("));
        Assert.Contains("animation.InsertExpressionKeyFrame(0f, \"this.StartingValue\");", code, StringComparison.Ordinal);
        Assert.Empty(System.Text.RegularExpressions.Regex.Matches(code, @"InsertKeyFrame\(\s*0f"));

        var requests = new Dictionary<string, string>
        {
            ["private void OnLoaded("] = "_planner.Request(_planner.PresentedTarget is null ? IndicatorCause.FirstLayout : IndicatorCause.Remount);",
            ["private void OnSelectionChanged("] = "_planner.Request(IndicatorCause.Selection);",
            ["private void OnLayoutChanged("] = "_planner.Request(IndicatorCause.Resize);",
            ["private void OnXamlRootChanged("] = "_planner.Request(IndicatorCause.DpiChange);",
            ["private void OnReducedMotionChanged("] = "_planner.Request(IndicatorCause.ReducedMotionChanged);",
        };
        foreach (var (handler, request) in requests)
        {
            var body = Body(code, handler);
            Assert.Contains(request, body, StringComparison.Ordinal);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"_planner\.Request\("));
        }

        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(code, @"_planner\.Request\(").Count);
    }

    /// <summary>T-1 (SaMotion): every enter/hover decision is fed the live Reduced Motion state.</summary>
    [Fact]
    public void T1_SaMotion_FeedsTheLiveReducedMotionStateToEveryDecision()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaMotion.cs");
        // Both call sites, and each one's LAST argument is the live MotionPolicy.IsReduced (never a constant).
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(code, @"MotionRules\.PlaysEnter\(").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(code, @"MotionRules\.PlaysEnter\([^;]*?, MotionPolicy\.IsReduced\)").Count);
        Assert.Contains("var transition = MotionPolicy.IsReduced ? null", Squash(Body(code, "private void ApplyHoverTransition(")), StringComparison.Ordinal);
        Assert.Contains("if (MotionPolicy.IsReduced) { ElementCompositionPreview.SetImplicitHideAnimation(_element, null);", Squash(Body(code, "private void ApplyHideAnimation(")), StringComparison.Ordinal);
    }

    /// <summary>T-2 (O6): the failure-path restore is exactly "only when NOT succeeded" (Cortex C-2).</summary>
    [Fact]
    public void T2_ModeChangeEnded_RestoresOnlyAFailedSwitch()
    {
        var body = Squash(Body(AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs"), "private void OnWindowModeChangeEnded("));

        Assert.Equal("{ if (!ended.Succeeded) { _compactSwitchAnnounced = false; StandardRoot.Opacity = 1; } }", body);
    }

    /// <summary>
    /// T-3 (O8/O9): the F07 remount fix's two load-bearing parts. SaLiveTree walks the ancestors to the XamlRoot content and
    /// never reads IsLoaded; an Unloaded only gives up the process-wide subscriptions - never the subtree's own events.
    /// </summary>
    [Fact]
    public void T3_TheRemountFix_LiveTreeWalk_AndAnUnloadedThatKeepsTheSubtreeEvents()
    {
        var live = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaLiveTree.cs");
        Assert.DoesNotContain("IsLoaded", live, StringComparison.Ordinal);
        Assert.Contains("var root = element.XamlRoot?.Content;", live, StringComparison.Ordinal);
        Assert.Contains("current = VisualTreeHelper.GetParent(current)", live, StringComparison.Ordinal);
        Assert.Contains("if (ReferenceEquals(current, root))", live, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(live, @"public static bool "));

        var controller = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaSlidingSelection.cs");
        var unloaded = Body(controller, "private void OnUnloaded(");
        Assert.DoesNotContain("ReleaseItems", unloaded, StringComparison.Ordinal);
        Assert.DoesNotContain("-=", unloaded, StringComparison.Ordinal);
        Assert.Contains("UnsubscribeExternal();", unloaded, StringComparison.Ordinal);
        var external = Body(controller, "private void UnsubscribeExternal(");
        Assert.All(System.Text.RegularExpressions.Regex.Matches(external, @"(\S+) -= ").Select(m => m.Groups[1].Value),
            target => Assert.Contains(target, new[] { "_xamlRoot.Changed", "MotionPolicy.Source.Changed" }));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(controller, @"ReleaseItems\(\);")); // only CollectItems re-collects

        var motion = Body(AppSourceTree.CodeWithoutComments("Controls/Primitives/SaMotion.cs"), "private void OnUnloaded(");
        Assert.All(System.Text.RegularExpressions.Regex.Matches(motion, @"(\S+) -= ").Select(m => m.Groups[1].Value),
            target => Assert.Equal("MotionPolicy.Source.Changed", target));
    }
}
