using System.Text.RegularExpressions;
using System.Xml.Linq;
using ServerMonitor.App.Views;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.8 fix round c2 (Beacon c1 B-1, Atlas phase 1 A-1 / A-3 and "other paths"). The view wiring the view-model tests
/// cannot see: B-1 was a green suite over a broken app - the row handler read DataContext, which the ItemsRepeater never
/// sets for an x:DataType template (it hands the item to the compiled ProcessBindings). Guarded at three levels: the XAML
/// (what is bound), the COMPILED x:Bind code the build generated from it (what actually runs per realized/recycled item),
/// and the handler's code. Plus the entry-focus wait's lifecycle, behaviourally, and where the window ends it.
/// </summary>
public sealed partial class Ui8CompactC2Tests
{
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    // ---- B-1 / A-1: the row passes ITS id through the compiled binding ----

    [Fact]
    public void TheRow_BindsItsIdAsTheCommandParameter_AndClicksThroughTheActivation_NeverTheDataContext()
    {
        var row = RowButton();
        Assert.Equal("OnRowClick", (string?)row.Attribute("Click"));
        Assert.Equal("{x:Bind ServerId}", (string?)row.Attribute("CommandParameter"));
        Assert.Null(row.Attribute("Command")); // the exit lives on the window-mode VM, out of the item template's reach

        var code = AppSourceTree.CodeWithoutComments("Controls/CompactShell.xaml.cs");
        Assert.Contains(
            "private void OnRowClick(object sender, RoutedEventArgs e) =>\n        CompactRowActivation.Open((sender as ButtonBase)?.CommandParameter, WindowMode?.OpenServerDetailCommand);",
            code.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.DoesNotContain("DataContext", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The code the XAML compiler generated for THIS build: each item handed to the row template (a new or a recycled
    /// element) runs Update_ServerId, which writes the row button's CommandParameter; and the row button's Click is wired
    /// to OnRowClick. A row template that drops or renames the id binding regenerates this file without it.
    /// </summary>
    [Fact]
    public void TheCompiledBinding_WritesEachItemsIdIntoTheRowButton_AndWiresItsClick()
    {
        var generated = File.ReadAllText(GeneratedShell());

        Assert.Matches(UpdateServerIdFromItem(), generated);
        var update = generated[generated.IndexOf("private void Update_ServerId(global::System.Guid obj, int phase)", StringComparison.Ordinal)..];
        update = update[..update.IndexOf("\n            }", StringComparison.Ordinal)];
        Assert.Matches(RowCommandParameterSetter(), update);
        Assert.Matches(RowClickWiring(), generated);
    }

    // ---- Atlas "other paths": the view's other bindings ----

    [Fact]
    public void TheStateActions_Expand_AndTheSwitch_AreBoundToTheWindowModeVm()
    {
        var shell = AppSourceTree.LoadXaml("Controls/CompactShell.xaml");
        Assert.Equal("{x:Bind WindowMode.AddServerCommand, Mode=OneWay}", (string?)Named(shell, "AddServerButton").Attribute("Command"));
        Assert.Equal("{x:Bind WindowMode.ManageHiddenServersCommand, Mode=OneWay}", (string?)Named(shell, "ManageHiddenButton").Attribute("Command"));
        // TwoWay: the switch gesture writes the preference (OneWay would only display it).
        Assert.Equal("{x:Bind WindowMode.CompactAlwaysOnTop, Mode=TwoWay}", (string?)Named(shell, "AlwaysOnTopSwitch").Attribute("IsOn"));

        var window = AppSourceTree.LoadXaml("MainWindow.xaml");
        Assert.Equal("{x:Bind ModeView.ExitCompactCommand}", (string?)Named(window, "CompactExpandButton").Attribute("Command"));
    }

    [Fact]
    public void TheList_IsBoundToTheRows_AndTheShellRefreshesItsBindingsOnceConnected()
    {
        var repeater = Named(AppSourceTree.LoadXaml("Controls/CompactShell.xaml"), "CompactRepeater");
        Assert.Equal("{x:Bind Presentation.Rows, Mode=OneWay}", (string?)repeater.Attribute("ItemsSource"));
        Assert.Equal("{StaticResource CompactServerRowTemplate}", (string?)repeater.Attribute("ItemTemplate"));

        var shell = AppSourceTree.CodeWithoutComments("Controls/CompactShell.xaml.cs");
        var initialize = Body(shell, "public void Initialize(CompactPresentationViewModel presentation, WindowModeViewModel windowMode)");
        Assert.True(initialize.IndexOf("WindowMode = windowMode", StringComparison.Ordinal) is var assigned and >= 0
            && initialize.IndexOf("Bindings.Update();", StringComparison.Ordinal) > assigned);

        var window = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");
        Assert.Contains("CompactShellView.Initialize(compactPresentation, windowModeViewModel);", window, StringComparison.Ordinal);
    }

    /// <summary>CPU, RAM and DISCO each in their own column, never swapped; the bar from the known-aware value (R-3).</summary>
    [Theory]
    [InlineData(0, "Cpu", "Cpu")]
    [InlineData(1, "Memory", "Memory")]
    [InlineData(2, "Disk", "Disk")]
    public void EachMetricColumn_ShowsItsOwnMetric_AndItsBarKnowsUnknownFromZero(int column, string label, string metric)
    {
        var columns = RowButton().Descendants()
            .Where(e => e.Name.LocalName == "StackPanel" && (string?)e.Attribute("Height") == "34")
            .ToList();
        var cell = Assert.Single(columns, e => ((string?)e.Attribute("Grid.Column") ?? "0") == column.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var texts = cell.Descendants().Where(e => e.Name.LocalName == "TextBlock").ToList();

        Assert.Equal($"CompactMetric{label}Label", (string?)texts[0].Attribute(AppSourceTree.Xaml + "Uid"));
        Assert.Equal($"{{x:Bind {metric}Text, Mode=OneWay}}", (string?)texts[1].Attribute("Text"));
        Assert.Equal($"{{x:Bind converters:CompactRowStyles.Value(Is{metric}Known, {metric}Severity, ShowsStaleCue), Mode=OneWay}}", (string?)texts[1].Attribute("Style"));
        var bar = Assert.Single(cell.Descendants(), e => e.Name.LocalName == "SaCompactMetricBar");
        Assert.Equal($"{{x:Bind converters:CompactRowStyles.BarValue(Is{metric}Known, {metric}BarValue), Mode=OneWay}}", (string?)bar.Attribute("Value"));
        Assert.Equal($"{{x:Bind converters:CompactRowStyles.Tone(Is{metric}Known, {metric}Severity), Mode=OneWay}}", (string?)bar.Attribute("Tone"));
    }

    // ---- A-3: the entry-focus wait's lifecycle (behaviour) and where the window ends it ----

    [Fact]
    public void TheWait_SubscribesOnce_AndLeavingCompactDisarmsIt_ALateLayoutPassMovesNothing()
    {
        var source = new LayoutSource();
        var attempts = 0;
        var wait = new CompactEntryFocusWait(source.Add, source.Remove, _ => { attempts++; return false; });

        wait.Begin();
        wait.Begin(); // a second entry re-arms, never stacks
        Assert.True(wait.IsWaiting);
        Assert.Equal(1, source.Count);

        var captured = source.Snapshot(); // a pass the layout system had already captured
        wait.End();
        Assert.False(wait.IsWaiting);
        Assert.Equal(0, source.Count);

        var before = attempts;
        foreach (var handler in captured)
        {
            handler(null, new object());
        }

        Assert.Equal(before, attempts); // no late focus attempt after leaving / closing
        wait.End(); // idempotent
        Assert.Equal(0, source.Count);
    }

    [Fact]
    public void TheWait_DisarmsAfterSuccess_WithTheLayoutPassCount()
    {
        var source = new LayoutSource();
        var passesSeen = new List<int>();
        var wait = new CompactEntryFocusWait(source.Add, source.Remove, passes => { passesSeen.Add(passes); return passes == 2; });

        wait.Begin();
        source.Raise();
        source.Raise();

        Assert.Equal([0, 1, 2], passesSeen);
        Assert.False(wait.IsWaiting);
        Assert.Equal(0, source.Count);
        source.Raise();
        Assert.Equal(3, passesSeen.Count);
    }

    [Fact]
    public void TheWait_FallsBackAfterTheBoundedPasses_AndDisarms()
    {
        var source = new LayoutSource();
        var targets = new List<CompactEntryFocus.Target>();
        var wait = new CompactEntryFocusWait(source.Add, source.Remove, passes =>
        {
            var target = CompactEntryFocus.Decide(hasRows: true, firstRowReady: false, hasAction: false, actionReady: false, passes);
            targets.Add(target);
            return target != CompactEntryFocus.Target.Wait;
        });

        wait.Begin();
        for (var pass = 0; pass < CompactEntryFocus.MaxLayoutPasses + 3; pass++)
        {
            source.Raise();
        }

        Assert.Equal(CompactEntryFocus.MaxLayoutPasses + 1, targets.Count);
        Assert.Equal(CompactEntryFocus.Target.Expand, targets[^1]);
        Assert.All(targets.SkipLast(1), target => Assert.Equal(CompactEntryFocus.Target.Wait, target));
        Assert.False(wait.IsWaiting);
        Assert.Equal(0, source.Count);
    }

    [Fact]
    public void ATargetAlreadyLaidOut_IsFocusedAtOnce_WithNoSubscription()
    {
        var source = new LayoutSource();
        var wait = new CompactEntryFocusWait(source.Add, source.Remove, _ => true);

        wait.Begin();

        Assert.False(wait.IsWaiting);
        Assert.Equal(0, source.Adds);
    }

    [Fact]
    public void TheWindow_EndsTheWait_OnLeavingCompact_EvenInactive_AndOnClose()
    {
        var code = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");

        Assert.Contains("private void EndCompactEntryFocus() => _compactEntryWait.End();", code, StringComparison.Ordinal);
        Assert.Contains("handler => CompactShellView.LayoutUpdated += handler,", code, StringComparison.Ordinal);
        Assert.Contains("handler => CompactShellView.LayoutUpdated -= handler,", code, StringComparison.Ordinal);

        var change = Body(code, "private void FocusAfterModeChange(WindowMode mode)");
        var leave = change.IndexOf("if (mode != WindowMode.Compact)", StringComparison.Ordinal);
        var end = change.IndexOf("EndCompactEntryFocus();", StringComparison.Ordinal);
        var activeGuard = change.IndexOf("if (!_isWindowActive)", StringComparison.Ordinal);
        Assert.True(leave >= 0 && end > leave && activeGuard > end, "leaving Compact must end the wait before the active-window guard");

        Assert.Contains("EndCompactEntryFocus();", Body(code, "private void OnWindowClosed(object sender, WindowEventArgs args)"), StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static XElement RowButton()
    {
        var shell = AppSourceTree.LoadXaml("Controls/CompactShell.xaml");
        var template = shell.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "CompactServerRowTemplate");
        return template.Elements().Single(e => e.Name.LocalName == "ServerTableRowButton");
    }

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == name);

    private static string GeneratedShell()
    {
        var path = Path.Combine(AppSourceTree.AppRoot, "obj", "x64", Configuration, "net10.0-windows10.0.19041.0", "win-x64", "Controls", "CompactShell.g.cs");
        Assert.True(File.Exists(path), $"the XAML compiler output is missing: {path}");
        return path;
    }

    // The method body that starts at the signature (brace-matched).
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"not found: {signature}");
        var open = code.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            depth += code[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return code[open..(i + 1)];
            }
        }

        throw new InvalidOperationException($"unbalanced: {signature}");
    }

    [GeneratedRegex(@"this\.Update_ServerId\(obj\.ServerId, phase\);")]
    private static partial Regex UpdateServerIdFromItem();

    [GeneratedRegex(@"Set_Microsoft_UI_Xaml_Controls_Primitives_ButtonBase_CommandParameter\(\(this\.obj\d+\.Target as global::ServerMonitor\.App\.Controls\.ServerTableRowButton\), obj, null\);")]
    private static partial Regex RowCommandParameterSetter();

    [GeneratedRegex(@"\(\(global::ServerMonitor\.App\.Controls\.ServerTableRowButton\)element\d+\)\.Click \+= this\.OnRowClick;")]
    private static partial Regex RowClickWiring();

    /// <summary>A LayoutUpdated stand-in that counts its handlers.</summary>
    private sealed class LayoutSource
    {
        private readonly List<EventHandler<object>> _handlers = [];

        public int Count => _handlers.Count;

        public int Adds { get; private set; }

        public void Add(EventHandler<object> handler)
        {
            Adds++;
            _handlers.Add(handler);
        }

        public void Remove(EventHandler<object> handler) => _handlers.Remove(handler);

        public IReadOnlyList<EventHandler<object>> Snapshot() => [.. _handlers];

        public void Raise()
        {
            foreach (var handler in Snapshot())
            {
                handler(null, new object());
            }
        }
    }
}
