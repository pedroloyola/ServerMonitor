using System.Globalization;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Views;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.8 fix round c1 (Prism c1 P-1..P-4). P-1: the row is 80 tall (padding 10 + status 18 + gap 8 + metric column FIXED 34
/// + 10) and the list steps by 88 - computed from the XAML and the tokens, not asserted as a magic number. P-2: where and
/// how focus lands on entering Compact (pure decision + the window's wiring). P-3: the state actions are at least 188 wide,
/// "Expandir" still hugs. P-4: the name tooltip only when the name is trimmed.
/// </summary>
public sealed class Ui8CompactC1Tests
{
    // ---- P-1 ----

    [Fact]
    public void TheRow_Is80Tall_AndTheListSteps88()
    {
        var shell = AppSourceTree.LoadXaml("Controls/CompactShell.xaml");
        var template = shell.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "CompactServerRowTemplate");
        var content = template.Elements().Single().Elements().Single(e => e.Name.LocalName == "StackPanel");
        var parts = content.Elements().ToList();
        var status = parts[0];
        var metrics = parts[1];
        var columns = metrics.Elements().Where(e => e.Name.LocalName == "StackPanel").ToList();

        Assert.Equal(3, columns.Count);
        Assert.All(columns, column => Assert.Equal("34", (string?)column.Attribute("Height")));
        Assert.Equal("18", (string?)status.Attribute("Height"));
        Assert.Equal("{StaticResource SaSpace8}", (string?)content.Attribute("Spacing"));

        var vertical = VerticalPadding("SaCompactRowButtonStyle");
        var gap = Space("SaSpace8");
        var row = vertical + 18 + gap + 34 + vertical;
        Assert.Equal(80, row);
        var layout = shell.Descendants().Single(e => e.Name.LocalName == "StackLayout");
        Assert.Equal(88, row + Space(((string)layout.Attribute("Spacing")!).Replace("{StaticResource ", string.Empty).TrimEnd('}')));
        // The stale cue (DV-10) is its own band BELOW the 34 column, never inside it.
        Assert.Equal("{x:Bind ShowsStaleCue, Mode=OneWay}", (string?)parts[2].Attribute("Visibility"));
    }

    // ---- P-2 ----

    [Theory]
    [InlineData(true, true, false, false, 0, "FirstRow")]
    [InlineData(true, false, false, false, 0, "Wait")]       // rows not realized yet: wait, no fallback
    [InlineData(true, false, false, false, 7, "Wait")]
    [InlineData(true, false, false, false, 8, "Expand")]     // bounded by layout passes, not by time
    [InlineData(false, false, true, true, 0, "StateAction")]
    [InlineData(false, false, true, false, 0, "Wait")]       // the action is not laid out yet
    [InlineData(false, false, true, false, 8, "Expand")]
    [InlineData(false, false, false, false, 0, "Expand")]    // loading / configuration unavailable
    public void TheEntryTarget_IsTheFirstRowOrTheRealAction_WaitingForLayout(bool rows, bool rowReady, bool action, bool actionReady, int passes, string expected) =>
        Assert.Equal(expected, CompactEntryFocus.Decide(rows, rowReady, action, actionReady, passes).ToString());

    [Fact]
    public void TheFocusRing_OnlyFollowsAKeyboardEntry()
    {
        Assert.Equal(FocusState.Keyboard, CompactEntryFocus.StateFor(enteredByKeyboard: true));
        Assert.Equal(FocusState.Programmatic, CompactEntryFocus.StateFor(enteredByKeyboard: false));
    }

    [Fact]
    public void TheWindow_UsesTheDecision_WaitsOnLayoutNotATimer_AndFocusesProgrammaticallyAtLaunch()
    {
        var code = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");

        Assert.Contains("BeginCompactEntryFocus(CompactEntryFocus.StateFor(_lastInputWasKeyboard));", code, StringComparison.Ordinal);
        Assert.Contains("CompactShellView.LayoutUpdated += OnCompactEntryLayoutUpdated;", code, StringComparison.Ordinal);
        Assert.Contains("CompactShellView.LayoutUpdated -= OnCompactEntryLayoutUpdated;", code, StringComparison.Ordinal);
        Assert.Contains("CompactEntryFocus.Decide(", code, StringComparison.Ordinal);
        Assert.Contains("RootLayout.AddHandler(UIElement.KeyDownEvent, _keyInputObserver, handledEventsToo: true);", code, StringComparison.Ordinal);
        Assert.Contains("RootLayout.AddHandler(UIElement.PointerPressedEvent, _pointerInputObserver, handledEventsToo: true);", code, StringComparison.Ordinal);
        var loaded = code[code.IndexOf("private async void OnRootLayoutLoaded", StringComparison.Ordinal)..code.IndexOf("private void OnWindowModeChanged", StringComparison.Ordinal)];
        Assert.Contains("BeginCompactEntryFocus(FocusState.Programmatic);", loaded, StringComparison.Ordinal);
        var entry = code[code.IndexOf("private bool TryCompactEntryFocus", StringComparison.Ordinal)..];
        Assert.DoesNotContain("FocusState.Keyboard", entry[..entry.IndexOf("\n    }", StringComparison.Ordinal)], StringComparison.Ordinal);
        // "Never steal focus": a background mode change still requires the window to hold focus already.
        var change = code[code.IndexOf("private void FocusAfterModeChange", StringComparison.Ordinal)..];
        Assert.True(change.IndexOf("GetFocusedElement(xamlRoot) is null", StringComparison.Ordinal) is var guard and >= 0
            && guard < change.IndexOf("BeginCompactEntryFocus", StringComparison.Ordinal));
    }

    // ---- P-3 / P-4 ----

    [Fact]
    public void TheStateActions_AreAtLeast188Wide_AndExpandStillHugs()
    {
        Assert.Equal("188", Setter("SaCompactButtonEmphasisStyle", "MinWidth"));
        Assert.Equal("36", Setter("SaCompactButtonStyle", "MinWidth"));
    }

    [Fact]
    public void TheNameTooltip_ExistsOnlyWhenTheNameIsTrimmed()
    {
        var shell = AppSourceTree.LoadXaml("Controls/CompactShell.xaml");
        var template = shell.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "CompactServerRowTemplate");

        Assert.DoesNotContain(template.Descendants(), e => e.Attributes().Any(a => a.Name.LocalName == "ToolTipService.ToolTip"));
        Assert.Single(template.Descendants(), e => (string?)e.Attribute("IsTextTrimmedChanged") == "OnNameTrimmedChanged");
        var code = AppSourceTree.CodeWithoutComments("Controls/CompactShell.xaml.cs");
        Assert.Contains("ToolTipService.SetToolTip(row, sender.IsTextTrimmed ? sender.Text : null);", code, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static XElement Style(string key) =>
        AppSourceTree.LoadXaml("Styles/Components/Sa.Primitives.xaml").Root!.Elements()
            .Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == key);

    private static string? Setter(string style, string property) =>
        (string?)Style(style).Elements().SingleOrDefault(e => (string?)e.Attribute("Property") == property)?.Attribute("Value");

    private static int VerticalPadding(string style)
    {
        var parts = Setter(style, "Padding")!.Split(',');
        return int.Parse(parts.Length == 1 ? parts[0] : parts[1], CultureInfo.InvariantCulture);
    }

    private static int Space(string key) =>
        int.Parse(AppSourceTree.LoadXaml("Styles/Tokens/Spacing.xaml").Root!.Elements()
            .Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == key).Value, CultureInfo.InvariantCulture);
}
