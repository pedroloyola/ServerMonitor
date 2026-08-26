using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Windowing;

namespace ServerMonitor.App.Tests.Windowing;

public sealed class WindowModeCoordinatorTests
{
    private static readonly DisplayWorkArea Primary = new(0, 0, 1920, 1040, 100);

    [Fact]
    public void Initialize_Standard_AppliesStandardPresenterBoundsAndNoTopmost()
    {
        var adapter = new FakeAdapter();
        var store = new FakeStore(new WindowPlacementSettings { Mode = WindowMode.Standard });
        var modes = new List<WindowMode>();
        var coordinator = Create(adapter, store);
        coordinator.ModeChanged += (_, mode) => modes.Add(mode);

        coordinator.Initialize();

        Assert.Equal(WindowMode.Standard, coordinator.CurrentMode);
        Assert.Equal(WindowMode.Standard, adapter.LastPresenterMode);
        Assert.True(adapter.ApplyBoundsCount >= 1);
        Assert.False(adapter.AlwaysOnTop);
        Assert.Equal([WindowMode.Standard], modes);
    }

    [Fact]
    public void Initialize_Compact_WithAlwaysOnTop_AppliesTopmost()
    {
        var adapter = new FakeAdapter();
        var store = new FakeStore(new WindowPlacementSettings
        {
            Mode = WindowMode.Compact,
            CompactAlwaysOnTop = true
        });
        var coordinator = Create(adapter, store);

        coordinator.Initialize();

        Assert.Equal(WindowMode.Compact, coordinator.CurrentMode);
        Assert.Equal(WindowMode.Compact, adapter.LastPresenterMode);
        Assert.True(adapter.AlwaysOnTop);
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        var adapter = new FakeAdapter();
        var coordinator = Create(adapter, new FakeStore());

        coordinator.Initialize();
        var afterFirst = adapter.ApplyBoundsCount;
        coordinator.Initialize();

        Assert.Equal(afterFirst, adapter.ApplyBoundsCount);
    }

    [Fact]
    public void SwitchBeforeInitialize_IsIgnored()
    {
        var adapter = new FakeAdapter();
        var coordinator = Create(adapter, new FakeStore());

        coordinator.SwitchTo(WindowMode.Compact);

        Assert.Equal(WindowMode.Standard, coordinator.CurrentMode);
        Assert.Equal(0, adapter.ApplyBoundsCount);
    }

    [Fact]
    public void SwitchToCompactThenBack_RestoresEachModesOwnBounds()
    {
        var adapter = new FakeAdapter();
        var store = new FakeStore();
        var coordinator = Create(adapter, store);

        adapter.CurrentBounds = new WindowBounds(120, 130, 800, 720); // where Standard sits
        coordinator.Initialize();

        // The user drags the (now standard) window somewhere, then switches to compact.
        adapter.CurrentBounds = new WindowBounds(200, 210, 780, 700);
        coordinator.CaptureCurrentBounds();
        coordinator.SwitchTo(WindowMode.Compact);
        Assert.Equal(WindowMode.Compact, coordinator.CurrentMode);

        // Move the compact window, switch back to standard: standard bounds must be restored.
        adapter.CurrentBounds = new WindowBounds(900, 400, 348, 420);
        coordinator.CaptureCurrentBounds();
        coordinator.SwitchTo(WindowMode.Standard);

        Assert.Equal(new WindowBounds(200, 210, 780, 700), adapter.LastAppliedBounds);

        // Forward to compact again restores the compact bounds, not the standard ones.
        coordinator.SwitchTo(WindowMode.Compact);
        Assert.Equal(new WindowBounds(900, 400, 348, 420), adapter.LastAppliedBounds);
    }

    [Fact]
    public void SwitchingModes_DoesNotOverwriteTheOtherModesBounds()
    {
        var store = new FakeStore();
        var adapter = new FakeAdapter { CurrentBounds = new WindowBounds(100, 100, 780, 700) };
        var coordinator = Create(adapter, store);
        coordinator.Initialize();

        adapter.CurrentBounds = new WindowBounds(100, 100, 780, 700);
        coordinator.CaptureCurrentBounds();
        coordinator.SwitchTo(WindowMode.Compact);
        adapter.CurrentBounds = new WindowBounds(700, 300, 348, 420);
        coordinator.CaptureCurrentBounds();
        coordinator.PersistCurrentBounds();

        Assert.NotNull(store.Saved.StandardBounds);
        Assert.NotNull(store.Saved.CompactBounds);
        Assert.NotEqual(store.Saved.StandardBounds, store.Saved.CompactBounds);
    }

    [Fact]
    public void AlwaysOnTop_IsCompactOnly_ButPersistedInStandard()
    {
        var adapter = new FakeAdapter();
        var store = new FakeStore();
        var coordinator = Create(adapter, store);
        coordinator.Initialize(); // Standard

        coordinator.SetCompactAlwaysOnTop(true);

        // In Standard the flag is remembered and persisted, but the window is not pinned.
        Assert.False(adapter.AlwaysOnTop);
        Assert.True(store.Saved.CompactAlwaysOnTop);
        Assert.True(coordinator.CompactAlwaysOnTop);

        // Entering compact applies it.
        coordinator.SwitchTo(WindowMode.Compact);
        Assert.True(adapter.AlwaysOnTop);

        // Leaving compact drops topmost.
        coordinator.SwitchTo(WindowMode.Standard);
        Assert.False(adapter.AlwaysOnTop);
    }

    [Fact]
    public void AlwaysOnTop_PersistsAcrossRestart()
    {
        var store = new FakeStore();
        var first = Create(new FakeAdapter(), store);
        first.Initialize();
        first.SwitchTo(WindowMode.Compact);
        first.SetCompactAlwaysOnTop(true);

        // A fresh coordinator over the same store simulates an app restart.
        var adapter = new FakeAdapter();
        var second = Create(adapter, store);
        second.Initialize();

        Assert.Equal(WindowMode.Compact, second.CurrentMode);
        Assert.True(second.CompactAlwaysOnTop);
        Assert.True(adapter.AlwaysOnTop);
    }

    [Fact]
    public void PersistWhileMinimized_KeepsLastGoodBounds()
    {
        var store = new FakeStore();
        var adapter = new FakeAdapter();
        var coordinator = Create(adapter, store);
        coordinator.Initialize();

        // The user positions the window, which is captured in memory.
        adapter.CurrentBounds = new WindowBounds(150, 160, 780, 700);
        coordinator.CaptureCurrentBounds();

        // The window minimizes: its geometry becomes unreadable, but persistence must keep the last
        // good rectangle rather than saving garbage.
        adapter.IsMinimized = true;
        coordinator.PersistCurrentBounds();

        Assert.Equal(new WindowBounds(150, 160, 780, 700), store.Saved.StandardBounds);
    }

    [Fact]
    public void RapidToggle_StaysConsistent_AndDoesNotLeakModeChangedHandlers()
    {
        var adapter = new FakeAdapter();
        var coordinator = Create(adapter, new FakeStore());
        var events = 0;
        coordinator.ModeChanged += (_, _) => events++;
        coordinator.Initialize();

        for (var i = 0; i < 20; i++)
        {
            coordinator.Toggle();
        }

        // 1 initial application + 20 toggles, each raising exactly one ModeChanged.
        Assert.Equal(21, events);
        Assert.Equal(WindowMode.Standard, coordinator.CurrentMode); // even number of toggles
    }

    private static WindowModeCoordinator Create(IWindowPlacementAdapter adapter, IWindowPlacementStore store) =>
        new(adapter, store, NullLogger<WindowModeCoordinator>.Instance);

    private sealed class FakeAdapter : IWindowPlacementAdapter
    {
        public bool IsMinimized { get; set; }

        public WindowBounds CurrentBounds { get; set; } = new(100, 100, 780, 760);

        public int CurrentDpi { get; set; } = 100;

        public IReadOnlyList<DisplayWorkArea> Displays { get; set; } = [Primary];

        public int ApplyBoundsCount { get; private set; }

        public WindowBounds LastAppliedBounds { get; private set; }

        public WindowMode LastPresenterMode { get; private set; }

        public bool AlwaysOnTop { get; private set; }

        public bool IsAttached => true;

        public WindowPlacement? GetPlacement() =>
            IsMinimized ? null : new WindowPlacement(CurrentBounds, CurrentDpi);

        public IReadOnlyList<DisplayWorkArea> GetDisplays() => Displays;

        public void ApplyBounds(WindowBounds bounds)
        {
            ApplyBoundsCount++;
            LastAppliedBounds = bounds;
            CurrentBounds = bounds; // the window moves to where it was told
        }

        public void ConfigurePresenter(WindowMode mode, WindowSizeConstraints constraints) =>
            LastPresenterMode = mode;

        public void SetAlwaysOnTop(bool enabled) => AlwaysOnTop = enabled;
    }

    private sealed class FakeStore(WindowPlacementSettings? initial = null) : IWindowPlacementStore
    {
        public WindowPlacementSettings Saved { get; private set; } = initial ?? WindowPlacementSettings.Default;

        public int SaveCount { get; private set; }

        public WindowPlacementSettings Load() => Saved;

        public void Save(WindowPlacementSettings settings)
        {
            SaveCount++;
            Saved = settings;
        }
    }
}
