using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// UI.11.1 H03 (Beacon S0 §1: after a theme change focus landed on "Sistema", not on the chosen theme). The batch is driven
/// against a fake window whose focus behaves like the measured one: when a root leaves the tree, focus falls to the first
/// focusable element ("Sistema"); restores run only when the queue is drained.
/// </summary>
public sealed class ThemeRemountFocusTests
{
    private sealed class FakeWindow : IRemountFocusPort
    {
        private readonly Queue<Action> _queue = new();

        public object? FocusedElement { get; set; }

        public RemountFocusKind Kind { get; set; }

        public List<(object Element, RemountFocusKind Kind)> Restores { get; } = [];

        public (object? Element, RemountFocusKind Kind) Focused() => (FocusedElement, FocusedElement is null ? RemountFocusKind.None : Kind);

        public void Restore(object element, RemountFocusKind kind)
        {
            Restores.Add((element, kind));
            FocusedElement = element;
            Kind = kind;
        }

        public void Post(Action action) => _queue.Enqueue(action);

        public void Drain()
        {
            while (_queue.Count > 0)
            {
                _queue.Dequeue()();
            }
        }
    }

    private const string Sistema = "Sistema";
    private const string Escuro = "Escuro";

    /// <summary>Four roots remounted in one pass; each one moves focus to "Sistema" like the real remount did.</summary>
    private static Func<bool>[] FourRootsMovingFocusTo(FakeWindow window, object fallback) =>
        Enumerable.Range(0, 4).Select(_ => (Func<bool>)(() =>
        {
            window.FocusedElement = fallback;
            window.Kind = RemountFocusKind.Pointer;
            return true;
        })).ToArray();

    [Theory]
    [InlineData(RemountFocusKind.Pointer)]
    [InlineData(RemountFocusKind.Keyboard)]
    [InlineData(RemountFocusKind.Programmatic)]
    public void TheChosenItemGetsFocusBack_Once_WithItsOwnFocusKind(RemountFocusKind kind)
    {
        var window = new FakeWindow { FocusedElement = Escuro, Kind = kind };

        Assert.Equal(4, ThemeRemountFocus.RemountAll(window, FourRootsMovingFocusTo(window, Sistema)));
        window.Drain();

        Assert.Equal([(Escuro, kind)], window.Restores);
        Assert.Equal(Escuro, window.FocusedElement);
    }

    [Fact]
    public void NothingFocused_NothingRestored_FocusIsNeverPulledIn()
    {
        var window = new FakeWindow();

        ThemeRemountFocus.RemountAll(window, FourRootsMovingFocusTo(window, Sistema));
        window.Drain();

        Assert.Empty(window.Restores);
    }

    [Fact]
    public void NoRemount_NoRestore()
    {
        var window = new FakeWindow { FocusedElement = Escuro, Kind = RemountFocusKind.Pointer };

        Assert.Equal(0, ThemeRemountFocus.RemountAll(window, [() => false, () => false]));
        window.Drain();

        Assert.Empty(window.Restores);
    }

    [Fact]
    public void TheRestoreWaitsForTheLayout_NeverDuringTheRemounts()
    {
        var window = new FakeWindow { FocusedElement = Escuro, Kind = RemountFocusKind.Pointer };

        ThemeRemountFocus.RemountAll(window, FourRootsMovingFocusTo(window, Sistema));

        Assert.Empty(window.Restores); // queued, not run inline
        window.Drain();
        Assert.Single(window.Restores);
    }
}
