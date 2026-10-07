using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Tests.ViewModels;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.7 final wave fix c2 (Beacon B-1..B-4, Prism C2-1..C2-4): the editor's layer focus trap and focus returns, the narrow
/// dialog commands and options card, the neutral running rings and the selected import row - held on the page's pure rules,
/// its code paths (the XAML runtime is not available here; Beacon verifies at runtime) and the XAML structure.
/// </summary>
public sealed class Ui7FinalC2Tests
{
    private static readonly XNamespace X = AppSourceTree.Xaml;

    private static string Page => AppSourceTree.CodeWithoutComments("Views/ServerEditorPage.xaml.cs");

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static string Method(string code, string signature, string next)
    {
        var body = code[code.IndexOf(signature, StringComparison.Ordinal)..];
        return body[..body.IndexOf(next, StringComparison.Ordinal)];
    }

    // ---- B-1: focus INTO the layer once laid out, then the page behind it is not interactive ----

    [Fact]
    public void B1_AFocusMove_IsRetriedOnLayoutPasses_Bounded_AndCompletesOnce()
    {
        var results = new List<bool>();
        var tries = 0;
        var attempt = new LayoutFocusAttempt(() => ++tries == 3, 8, results.Add);
        Assert.False(attempt.Step()); // not laid out yet: Focus() is false
        Assert.False(attempt.Step());
        Assert.True(attempt.Step());  // the third layout pass: focused
        Assert.True(attempt.Step());  // done: nothing more is tried
        Assert.Equal(3, tries);
        Assert.Equal([true], results);

        var never = new List<bool>();
        var giveUp = new LayoutFocusAttempt(() => false, 4, never.Add);
        var steps = 0;
        while (!giveUp.Step())
        {
            steps++;
        }

        Assert.Equal(3, steps);
        Assert.Equal(4, giveUp.Attempts);
        Assert.Equal([false], never);

        var cancelled = new List<bool>();
        var replaced = new LayoutFocusAttempt(() => false, 4, cancelled.Add);
        replaced.Cancel();
        Assert.True(replaced.Step());
        Assert.Empty(cancelled);
    }

    [Fact]
    public void B1_OpeningALayer_MovesFocusIntoItFirst_ThenDisablesThePageBehindIt()
    {
        var page = Page;
        var show = Method(page, "private void ShowLayer(ServerEditorLayer layer)", "private void UpdateDialogMaxWidth()");
        var open = show[show.IndexOf("if (opening)", StringComparison.Ordinal)..];
        // (b) no pointer reaches the page at once; (a) focus into the layer once laid out; (c) only then is the page disabled.
        var hitTest = open.IndexOf("PageRoot.IsHitTestVisible = false;", StringComparison.Ordinal);
        var focus = open.IndexOf("FocusWhenLaidOut(FocusLayer, _ =>", StringComparison.Ordinal);
        var disable = open.IndexOf("SetPageInteractive(false);", StringComparison.Ordinal);
        Assert.True(hitTest > 0 && focus > hitTest && disable > focus, "order: hit test off, focus into the layer, then disable");
        Assert.DoesNotContain("DispatcherQueue.TryEnqueue(() =>", open, StringComparison.Ordinal); // the old unlaid-out focus

        var interactive = Method(page, "private void SetPageInteractive(bool interactive)", "private void FocusWhenLaidOut");
        Assert.Contains("PageRoot.IsHitTestVisible = interactive;", interactive, StringComparison.Ordinal);
        Assert.Contains("PageScroll.IsEnabled = interactive;", interactive, StringComparison.Ordinal);

        var layer = Method(page, "private bool FocusLayer() => _layer switch", "private void HideLayer()");
        Assert.Contains("ServerEditorLayer.Test => TestPanel.FocusSafeButton(),", layer, StringComparison.Ordinal);
        Assert.Contains("ServerEditorLayer.Trust => TrustPanel.FocusSafeButton(),", layer, StringComparison.Ordinal);
        Assert.Contains("ServerEditorLayer.Import => ImportPanel.FocusInitial(),", layer, StringComparison.Ordinal);

        // Retries come from layout passes and one low-priority dispatch - never a timer or the clock.
        var retry = Method(page, "private void FocusWhenLaidOut(", "private void OnLayoutUpdatedFocus");
        Assert.Contains("LayoutUpdated += OnLayoutUpdatedFocus;", retry, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"Task\.Delay|DispatcherTimer|Stopwatch|DateTime|TimeSpan"), page);
    }

    [Fact]
    public void B1_EveryInteractiveElementOfThePage_IsInsidePageScroll_SoDisablingItLeavesNothingBehindTheLayer()
    {
        var xaml = AppSourceTree.LoadXaml("Views/ServerEditorPage.xaml");
        var scroll = Named(xaml, "PageScroll");
        var layer = Named(xaml, "DialogLayer");
        Assert.Equal(scroll.Parent, layer.Parent); // siblings: the layer is not disabled with the page
        Assert.True(scroll.ElementsAfterSelf().Contains(layer), "the layer is drawn over the page");
        foreach (var name in new[] { "ServerForm", "ActionArea", "TestButton", "CancelButton", "PrimaryButton", "HeaderButton", "OpenDuplicateButton", "RetrySaveButton" })
        {
            Assert.Contains(Named(xaml, name).Ancestors(), e => e == scroll);
        }

        Assert.Equal("ScrollViewer", scroll.Name.LocalName); // a Control: IsEnabled=false disables every control inside it
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]   // the focused control is never disabled under the focus (B-1 c / B-3)
    [InlineData(false, false, false)]
    public void B1_B3_TheFocusedControl_IsNotDisabledUnderTheFocus(bool wanted, bool hasFocus, bool enabled) =>
        Assert.Equal(enabled, ServerEditorPage.KeepsEnabledForFocus(wanted, hasFocus));

    [Fact]
    public void B1_B3_TheActionState_GoesThroughTheFocusRule_AndEveryKeptActionRefusesItself()
    {
        var page = Page;
        var state = Method(page, "private void UpdateActionState()", "internal static bool KeepsEnabledForFocus");
        foreach (var control in new[] { "TestButton", "PrimaryButton", "RetrySaveButton", "CancelButton", "HeaderButton" })
        {
            Assert.Contains($"SetEnabled({control},", state, StringComparison.Ordinal);
            Assert.DoesNotContain($"{control}.IsEnabled =", state, StringComparison.Ordinal);
        }

        Assert.Contains("control.IsEnabled = KeepsEnabledForFocus(enabled, control.FocusState != FocusState.Unfocused);", page, StringComparison.Ordinal);
        // Re-entry: Submit / Test / Cancel / Escape / OpenDuplicate are refused by the controller while saving or busy;
        // the header button checks it here.
        var controller = AppSourceTree.CodeWithoutComments("ViewModels/ServerEditorPageController.cs");
        Assert.Contains("if (_disposed || _saving || Request is null || ViewModel is not { } viewModel || viewModel.IsConnectionWorkInProgress)", controller, StringComparison.Ordinal);
        Assert.Contains("if (_disposed || _saving || ViewModel is not { } viewModel || viewModel.IsConnectionWorkInProgress)", controller, StringComparison.Ordinal);
        Assert.Contains("if (!_disposed && !_saving && Request is not null)", controller, StringComparison.Ordinal);
        var header = Method(page, "private async void OnHeaderButtonClick", "private async void Submit()");
        Assert.Matches(new Regex(@"if \(_controller\.IsSaving\)\s*\{\s*return;\s*\}"), header); // final c3: the check AND its return
        var guard = header.IndexOf("if (_controller.IsSaving)", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < header.IndexOf("_controller.Cancel();", StringComparison.Ordinal)
            && guard < header.IndexOf("_controller.OpenImportAsync();", StringComparison.Ordinal), "the header button refuses while saving, first");
    }

    // ---- B-2: "Continuar a editar" puts the focus back, every cycle ----

    [Fact]
    public async Task B2_KeepEditing_RestoresTheFocus_OnEveryCycle_NeverAfterDiscardOrAFailedQuestion()
    {
        var restored = 0;
        for (var cycle = 1; cycle <= 2; cycle++)
        {
            var question = new TaskCompletionSource<bool>();
            var watch = ServerEditorPage.RunIfKeptAsync(question.Task, () => true, () => restored++);
            Assert.Equal(cycle - 1, restored); // nothing before the answer
            question.SetResult(false);        // "Continuar a editar"
            await watch;
            Assert.Equal(cycle, restored);
        }

        var discard = new TaskCompletionSource<bool>();
        var discarded = ServerEditorPage.RunIfKeptAsync(discard.Task, () => true, () => restored++);
        discard.SetResult(true);
        await discarded;

        var failed = new TaskCompletionSource<bool>();
        var faulted = ServerEditorPage.RunIfKeptAsync(failed.Task, () => true, () => restored++);
        failed.SetException(new InvalidOperationException("no window"));
        await faulted;

        var gone = new TaskCompletionSource<bool>();
        var disposed = ServerEditorPage.RunIfKeptAsync(gone.Task, () => false, () => restored++);
        gone.SetResult(false);
        await disposed;

        Assert.Equal(2, restored);
    }

    [Fact]
    public void B2_ThePageCapturesTheFocusBeforeAsking_AndRestoresItOrTheFirstField()
    {
        var guard = Method(Page, "public Task<bool> ConfirmLeaveAsync()", "internal static async Task RunIfKeptAsync");
        var capture = guard.IndexOf("FocusManager.GetFocusedElement(root) as Control", StringComparison.Ordinal);
        var ask = guard.IndexOf("_controller.ConfirmLeaveAsync();", StringComparison.Ordinal);
        Assert.True(capture > 0 && ask > capture, "the focus is captured before the question opens");
        Assert.Contains("RunIfKeptAsync(answer, () => !_disposed, () => FocusWhenLaidOut(() => KeptFocusTarget(focused)));", guard, StringComparison.Ordinal);
        Assert.Contains("focused.Focus(FocusState.Programmatic)) || ServerForm.FocusFirstField();", Page, StringComparison.Ordinal);
    }

    // ---- B-3 / B-4: failed save and closed layers send the focus somewhere useful ----

    [Fact]
    public void B3_AFailedSave_FocusesTentarGuardar_OrTheFirstFieldWhenLocked()
    {
        var failed = Method(Page, "private void SetSaveFailed(", "\n}");
        Assert.Contains("FocusWhenLaidOut(() => locked ? ServerForm.FocusFirstField() : RetrySaveButton.Focus(FocusState.Programmatic));", failed, StringComparison.Ordinal);
        Assert.True(failed.IndexOf("SaveFailedNotice.Visibility = Visibility.Visible;", StringComparison.Ordinal)
            < failed.IndexOf("FocusWhenLaidOut(", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ServerEditorLayer.Import, true, true)]
    [InlineData(ServerEditorLayer.Import, false, false)] // no header button shown: "Testar ligação"
    [InlineData(ServerEditorLayer.Test, true, false)]
    [InlineData(ServerEditorLayer.Trust, true, false)]
    public void B4_ClosingALayer_ReturnsToItsTrigger(ServerEditorLayer opener, bool headerShown, bool toHeader) =>
        Assert.Equal(toHeader, ServerEditorPage.LayerReturnsToHeaderButton(opener, headerShown));

    [Fact]
    public void B4_HideLayer_ReEnablesThePageFirst_ThenFocusesTheTrigger()
    {
        var hide = Method(Page, "private void HideLayer()", "internal static bool LayerReturnsToHeaderButton");
        var enable = hide.IndexOf("SetPageInteractive(true);", StringComparison.Ordinal);
        var focus = hide.IndexOf("FocusWhenLaidOut(() => trigger.Focus(FocusState.Programmatic));", StringComparison.Ordinal);
        Assert.True(enable > 0 && focus > enable);
        Assert.Contains("LayerReturnsToHeaderButton(opener, HeaderButton.Visibility == Visibility.Visible) ? HeaderButton : TestButton", hide, StringComparison.Ordinal);
        Assert.DoesNotContain("TestButton.Focus(FocusState.Programmatic);", hide, StringComparison.Ordinal);
    }

    // ---- C2-1: at the 560 minimum the dialog commands stack instead of being cut ----

    [Theory]
    [InlineData(560)]
    [InlineData(640)]
    [InlineData(700)]
    [InlineData(1040)]
    [InlineData(1440)]
    public void C21_TheDialogCommands_AlwaysFit_SideBySideOrStacked(double window)
    {
        // Measured (Beacon c1, UIA): the page host is the window minus 96 at these widths (560 -> dialog 416, 700 -> 556).
        var host = window - 96;
        var margin = new Thickness(24, 40, 24, 24);
        var padding = new Thickness(28);
        foreach (var border in new[] { new Thickness(1), new Thickness(2) }) // HC draws 2
        {
            foreach (var layer in new[] { ServerEditorLayer.Test, ServerEditorLayer.Trust, ServerEditorLayer.Import })
            {
                var surface = Math.Min(ServerEditorPage.DialogWidth(layer), ServerEditorPage.DialogMaxWidth(host, margin));
                var content = surface - padding.Left - padding.Right - border.Left - border.Right;
                var stacked = ServerEditorPage.StackDialogCommands(surface, padding, border);
                Assert.True(stacked || content >= ServerEditorPage.DialogCommandsSideBySideWidth,
                    $"{layer} at {window}: {content} of content cannot hold two {ServerEditorPage.DialogCommandsSideBySideWidth} commands");
                if (window == 560)
                {
                    Assert.True(stacked, $"{layer} at 560 must stack");
                }

                if (window >= 1040)
                {
                    Assert.False(stacked, $"{layer} at {window} keeps Figma's side-by-side commands");
                }
            }
        }

        Assert.Equal((2 * 196) + 12, ServerEditorPage.DialogCommandsSideBySideWidth);
        var style = AppSourceTree.LoadXaml("Styles/Components/Sa.Dialogs.xaml").Descendants()
            .Single(e => (string?)e.Attribute(X + "Key") == "SaDialogCommandButtonStyle");
        Assert.Equal("196", (string?)style.Elements().Single(e => (string?)e.Attribute("Property") == "MinWidth").Attribute("Value"));
    }

    [Fact]
    public void C21_Stacked_TheDefaultActionComesFirst_AndEveryPanelAppliesIt()
    {
        Assert.Equal(("safe", "default"), DialogCommandRow.Order("safe", "default", stacked: false));
        Assert.Equal(("default", "safe"), DialogCommandRow.Order("safe", "default", stacked: true));
        Assert.Contains("DialogSurface.SizeChanged += (_, args) => UpdateDialogCommands(args.NewSize.Width);", Page, StringComparison.Ordinal);
        foreach (var (panel, safe, primary) in new[]
                 {
                     ("ServerEditorTestPanel", "CloseButton", "RetryButton"),
                     ("ServerEditorTrustPanel", "CloseButton", "AcceptButton"),
                     ("SshConfigImportPanel", "CancelButton", "UseButton")
                 })
        {
            Assert.Contains($"public void SetStackedCommands(bool stacked) => DialogCommandRow.Apply(ActionRow, {safe}, {primary}, stacked);",
                AppSourceTree.CodeWithoutComments($"Views/{panel}.xaml.cs"), StringComparison.Ordinal);
            Assert.Contains("TestPanel.SetStackedCommands(stacked);", Page, StringComparison.Ordinal);
            var row = Named(AppSourceTree.LoadXaml($"Views/{panel}.xaml"), "ActionRow");
            Assert.Equal([safe, primary], row.Elements().Select(e => (string)e.Attribute(X + "Name")!).ToArray());
        }
    }

    // ---- C2-2: the options card never pushes a selector out of the card ----

    [Fact]
    public void C22_NarrowOptionsCard_SelectorsFitTheirColumn_AndUnder420EachFieldTakesARow()
    {
        var form = AppSourceTree.LoadXaml("Controls/ServerFormControl.xaml");
        List<(string? Target, string? Value)> Setters(string state) => form.Descendants()
            .Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(X + "Name") == state)
            .Descendants().Where(e => e.Name.LocalName == "Setter")
            .Select(e => ((string?)e.Attribute("Target"), (string?)e.Attribute("Value"))).ToList();
        double Trigger(string state, string bound) => double.Parse((string)form.Descendants()
            .Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(X + "Name") == state)
            .Descendants().Single(e => e.Name.LocalName == "SaContentWidthTrigger").Attribute(bound)!);

        foreach (var state in new[] { "Stacked", "Compact" })
        {
            Assert.Contains(("OperatingSystemSelector.MinWidth", "0"), Setters(state));
            Assert.Contains(("RefreshIntervalSelector.MinWidth", "0"), Setters(state));
        }

        Assert.Equal(420, Trigger("Stacked", "MinWidth"));
        Assert.Equal(420, Trigger("Compact", "MaxWidth"));
        Assert.Contains(("OperatingSystemField.(Grid.Row)", "1"), Setters("Compact"));
        Assert.Contains(("RefreshIntervalField.(Grid.Row)", "2"), Setters("Compact"));
        Assert.Contains(("OptionsColumn1.Width", "*"), Setters("Compact"));
        Assert.Contains(("OptionsColumn2.Width", "0"), Setters("Compact"));
        // Every Stacked setter still applies in Compact (the cards stack there too).
        Assert.All(Setters("Stacked").Where(s => s.Target is not null && !s.Target.StartsWith("RefreshIntervalField", StringComparison.Ordinal)),
            setter => Assert.Contains(setter, Setters("Compact")));
        Assert.Equal(3, Named(form, "OptionsGrid").Elements().Single(e => e.Name.LocalName == "Grid.RowDefinitions").Elements().Count());

        // Geometry: from 420 of form width (card padding 24 + 24, Porta 160 + 16), SO keeps >= 196 - the old 200
        // minimum overflowed exactly there; below 420 SO has the whole row.
        const double cardPadding = 48, port = 160, gap = 16;
        Assert.True(Trigger("Stacked", "MinWidth") - cardPadding - port - gap >= 196);
        Assert.Equal("160", (string?)form.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "OptionsColumn1").Attribute("Width"));
    }

    // ---- C2-3 / C2-4: neutral running rings; the selected import row is visible and says so ----

    [Theory]
    [InlineData("Views/ServerEditorTestPanel.xaml", null)]
    [InlineData("Views/ServerEditorTrustPanel.xaml", "WorkingRing")]
    [InlineData("Views/SshConfigImportPanel.xaml", "LoadingRing")]
    public void C23_TheRunningRings_AreNeutralText(string file, string? name)
    {
        var rings = AppSourceTree.LoadXaml(file).Descendants().Where(e => e.Name.LocalName == "ProgressRing").ToList();
        Assert.NotEmpty(rings);
        if (name is not null)
        {
            Assert.Contains(rings, ring => (string?)ring.Attribute(X + "Name") == name);
        }

        Assert.All(rings, ring => Assert.Equal("{ThemeResource SaTextBrush}", (string?)ring.Attribute("Foreground")));
    }

    [Fact]
    public void C24_TheSelectedRow_DrawsSaSelectedBrushOverTheTint_InEveryFocusState()
    {
        var surfaces = AppSourceTree.LoadXaml("Styles/Components/Sa.Surfaces.xaml");
        var style = surfaces.Descendants().Single(e => (string?)e.Attribute(X + "Key") == "SaInsetListItemStyle");
        var overlay = style.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "SelectedOverlay");
        Assert.Equal("{ThemeResource SaSelectedBrush}", (string?)overlay.Attribute("Background"));
        Assert.Equal("0", (string?)overlay.Attribute("Opacity"));
        foreach (var state in new[] { "Selected", "PointerOverSelected", "PressedSelected" })
        {
            var setters = style.Descendants().Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(X + "Name") == state)
                .Descendants().Where(e => e.Name.LocalName == "Setter").ToList();
            Assert.Contains(setters, s => (string?)s.Attribute("Target") == "SelectedOverlay.Opacity" && (string?)s.Attribute("Value") == "1");
        }

        foreach (var state in new[] { "Normal", "PointerOver", "Pressed" })
        {
            Assert.DoesNotContain(style.Descendants().Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(X + "Name") == state)
                .Descendants(), s => (string?)s.Attribute("Target") == "SelectedOverlay.Opacity");
        }

        // The tint stays underneath (drawn by the root), the content above the overlays.
        var root = style.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "RowRoot");
        Assert.Equal("{TemplateBinding Background}", (string?)root.Attribute("Background"));
        Assert.Equal("ContentPresenter", root.Elements().Last().Name.LocalName);
        Assert.DoesNotContain(style.Descendants(), e => e.Name.LocalName == "ListViewItemPresenter"); // no accent pill
    }

    [Theory]
    [InlineData(true, false, SshConfigImportRowState.Available)]
    [InlineData(true, true, SshConfigImportRowState.Selected)]
    [InlineData(false, true, SshConfigImportRowState.Blocked)]
    [InlineData(false, false, SshConfigImportRowState.Blocked)]
    public void C24_TheStateColumn_SaysSelectedForTheSelectedImportableRow(bool importable, bool selected, SshConfigImportRowState expected) =>
        Assert.Equal(expected, SshConfigImportPresentation.RowState(importable, selected));

    [Fact]
    public void C24_TheSelectedWord_IsWrittenInEveryCulture_AndSetPerContainer()
    {
        Ui7Resources.AssertPresentInEveryCulture(["ServerEditorImportSelected.Text"]);
        Assert.Equal("Selecionado", Ui7Resources.Load("pt-PT")["ServerEditorImportSelected.Text"]);
        var panel = AppSourceTree.CodeWithoutComments("Views/SshConfigImportPanel.xaml.cs");
        Assert.Contains("SshConfigImportPresentation.RowState(option.IsImportable, container.IsSelected)", panel, StringComparison.Ordinal);
        Assert.Contains("foreach (var item in e.RemovedItems.Concat(e.AddedItems))", panel, StringComparison.Ordinal);
        var available = Named(AppSourceTree.LoadXaml("Views/SshConfigImportPanel.xaml"), "AvailableText");
        Assert.Equal("Collapsed", (string?)available.Attribute("Visibility")); // set per container, never a stale x:Bind
    }
}
