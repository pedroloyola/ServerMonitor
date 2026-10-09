using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Windowing;

/// <summary>
/// UI.11 F16 (Prism rev.4 D-B3, Cortex verdict SAFE_WITH_CONDITIONS, tests 1-5). A switch to Compact is ANNOUNCED
/// (ModeChanging) before the first window step, so MainWindow can hide the Standard content before the resize shows it
/// clipped; the announcement is always closed from the finally (ModeChangeEnded), also when a window step throws; nothing
/// else in the UI.8 transition changes (order, B-7, ModeChanged semantics, synchronous SwitchTo).
/// </summary>
public sealed class Ui11CompactEntryTests
{
    private static (RecordingPlacementAdapter Adapter, WindowModeCoordinator Coordinator, List<string> Log) Create(IWindowPlacementAdapter? adapter = null)
    {
        var recording = new RecordingPlacementAdapter();
        var coordinator = new WindowModeCoordinator(adapter ?? recording, new FakeWindowPlacementStore(), NullLogger<WindowModeCoordinator>.Instance);
        var log = recording.Calls;
        coordinator.ModeChanging += (_, mode) => log.Add($"ModeChanging {mode}");
        coordinator.ModeChangeEnded += (_, ended) => log.Add($"ModeChangeEnded {ended.Target} {ended.Succeeded}");
        coordinator.ModeChanged += (_, mode) => log.Add($"ModeChanged {mode}");
        return (recording, coordinator, log);
    }

    [Fact]
    public void Test1_TheAnnouncementPrecedesRestore_PresenterAndBounds_OfAMaximizedStandard()
    {
        var (adapter, coordinator, log) = Create();
        coordinator.Initialize();
        adapter.Maximize();
        log.Clear();

        coordinator.SwitchTo(WindowMode.Compact);

        var steps = log.Select(entry => entry.Split(' ')[0]).ToList();
        Assert.Equal(["ModeChanging", "Restore", "ConfigurePresenter", "ApplyBounds", "ModeChangeEnded", "ModeChanged"], steps);
        Assert.Equal("ModeChanging Compact", log[0]);
        Assert.Equal("ModeChangeEnded Compact True", log[^2]);
    }

    [Fact]
    public void Test1_NotMaximized_TheAnnouncementPrecedesPresenterAndBounds()
    {
        var (_, coordinator, log) = Create();
        coordinator.Initialize();
        log.Clear();

        coordinator.SwitchTo(WindowMode.Compact);

        Assert.Equal(["ModeChanging", "ConfigurePresenter", "ApplyBounds", "ModeChangeEnded", "ModeChanged"], log.Select(entry => entry.Split(' ')[0]));
    }

    [Fact]
    public void Test2_NoAnnouncement_FromInitialize_ASameModeSwitch_TheHold_OrBeforeInitialize_OrTowardsStandard()
    {
        var store = new FakeWindowPlacementStore(new WindowPlacementSettings { Mode = WindowMode.Compact });
        var adapter = new RecordingPlacementAdapter();
        var coordinator = new WindowModeCoordinator(adapter, store, NullLogger<WindowModeCoordinator>.Instance);
        var announced = 0;
        coordinator.ModeChanging += (_, _) => announced++;
        coordinator.ModeChangeEnded += (_, _) => announced++;

        coordinator.SwitchTo(WindowMode.Standard); // before Initialize: ignored
        coordinator.Initialize();                   // launch straight into Compact
        coordinator.SwitchTo(WindowMode.Compact);   // same mode
        adapter.IsMaximized = true;
        Assert.True(coordinator.HoldCompactRestored()); // B-2 hold is not a mode change
        coordinator.SwitchTo(WindowMode.Standard);  // Compact -> Standard stays unannounced (C-4)

        Assert.Equal(0, announced);
    }

    [Fact]
    public void Test3_AWindowStepThrows_TheAnnouncementIsStillClosed_NoModeChanged_NotApplying()
    {
        var thrower = new ThrowingAdapter();
        var coordinator = new WindowModeCoordinator(thrower, new FakeWindowPlacementStore(), NullLogger<WindowModeCoordinator>.Instance);
        var log = new List<string>();
        coordinator.ModeChanging += (_, mode) => log.Add($"ModeChanging {mode}");
        coordinator.ModeChangeEnded += (_, ended) => log.Add($"ModeChangeEnded {ended.Target} {ended.Succeeded}");
        coordinator.ModeChanged += (_, mode) => log.Add($"ModeChanged {mode}");
        coordinator.Initialize();
        log.Clear();
        thrower.ThrowOnApplyBounds = true;

        Assert.Throws<InvalidOperationException>(() => coordinator.SwitchTo(WindowMode.Compact));

        Assert.Equal(["ModeChanging Compact", "ModeChangeEnded Compact False"], log);
        Assert.False(coordinator.IsApplyingBounds);
    }

    [Fact]
    public void Test4_RapidToggle_OneAnnouncementPerCompactEntry_NoLeak()
    {
        var (_, coordinator, log) = Create();
        coordinator.Initialize();
        log.Clear();

        for (var i = 0; i < 20; i++)
        {
            coordinator.Toggle();
        }

        Assert.Equal(10, log.Count(entry => entry == "ModeChanging Compact"));
        Assert.Equal(10, log.Count(entry => entry == "ModeChangeEnded Compact True"));
        Assert.Equal(20, log.Count(entry => entry.StartsWith("ModeChanged", StringComparison.Ordinal)));
        Assert.Equal(WindowMode.Standard, coordinator.CurrentMode);
    }

    /// <summary>Test 5 (source, Cortex C-2/C-3/C-4/C-6): the subscriber only hides; both branches restore; the failure path restores.</summary>
    [Fact]
    public void Test5_MainWindow_HidesOnlyWithOpacity_AndAlwaysRestoresIt()
    {
        var code = AppSourceTree.CodeWithoutComments("MainWindow.xaml.cs");

        // C-3 (+ Cortex R-3): the subscriber records the announced switch and hides with ONE Opacity write - nothing else.
        var changing = Body(code, "private void OnWindowModeChanging(");
        Assert.Equal(
            ["_compactSwitchAnnounced = true;", "StandardRoot.Opacity = 0;"],
            changing.Trim('{', '}').Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(statement => statement.Length > 0).Select(statement => statement + ";"));
        var ended = Body(code, "private void OnWindowModeChangeEnded(");
        Assert.Contains("StandardRoot.Opacity = 1;", ended, StringComparison.Ordinal);
        var changed = Body(code, "private void OnWindowModeChanged(");
        var compact = changed[..changed.IndexOf("else", StringComparison.Ordinal)];
        var standard = changed[changed.IndexOf("else", StringComparison.Ordinal)..];
        Assert.Contains("StandardRoot.Opacity = 1;", compact, StringComparison.Ordinal);
        Assert.Contains("StandardRoot.Opacity = 1;", standard, StringComparison.Ordinal);
        Assert.Contains("SaMotion.PlayEnter(CompactRoot, SaMotionEnter.Fade);", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayEnter", standard, StringComparison.Ordinal); // Compact -> Standard stays instant
        Assert.DoesNotContain("StandardRoot.Opacity = 0", changed, StringComparison.Ordinal);
        Assert.Contains("_modeCoordinator.ModeChanging -= OnWindowModeChanging;", code, StringComparison.Ordinal);
        Assert.Contains("_modeCoordinator.ModeChangeEnded -= OnWindowModeChangeEnded;", code, StringComparison.Ordinal);
        // C-5: the coordinator's switch stays synchronous.
        var coordinator = AppSourceTree.CodeWithoutComments("Windowing/WindowModeCoordinator.cs");
        Assert.DoesNotContain("await", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherQueue", coordinator, StringComparison.Ordinal);
    }

    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var open = code.IndexOf('{', start);
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

    private sealed class ThrowingAdapter : IWindowPlacementAdapter
    {
        private readonly RecordingPlacementAdapter _inner = new();

        public bool ThrowOnApplyBounds { get; set; }

        public bool IsAttached => true;

        public bool IsMaximized => _inner.IsMaximized;

        public WindowPlacement? GetPlacement() => _inner.GetPlacement();

        public IReadOnlyList<DisplayWorkArea> GetDisplays() => _inner.GetDisplays();

        public void ApplyBounds(WindowBounds bounds)
        {
            if (ThrowOnApplyBounds)
            {
                throw new InvalidOperationException("simulated MoveAndResize failure");
            }

            _inner.ApplyBounds(bounds);
        }

        public void ConfigurePresenter(WindowMode mode, WindowSizeConstraints constraints) => _inner.ConfigurePresenter(mode, constraints);

        public WindowFrame GetFrame() => _inner.GetFrame();

        public void SetAlwaysOnTop(bool enabled) => _inner.SetAlwaysOnTop(enabled);

        public void Restore() => _inner.Restore();

        public void Maximize() => _inner.Maximize();
    }
}
