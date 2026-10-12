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
    private const string Claro = "Claro";
    private const string OtherControl = "OtherControl";
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

        Assert.Equal(4, new ThemeRemountFocus().RemountAll(window, FourRootsMovingFocusTo(window, Sistema)));
        window.Drain();

        Assert.Equal([(Escuro, kind)], window.Restores);
        Assert.Equal(Escuro, window.FocusedElement);
    }

    [Fact]
    public void NothingFocused_NothingRestored_FocusIsNeverPulledIn()
    {
        var window = new FakeWindow();

        new ThemeRemountFocus().RemountAll(window, FourRootsMovingFocusTo(window, Sistema));
        window.Drain();

        Assert.Empty(window.Restores);
    }

    [Fact]
    public void NoRemount_NoRestore()
    {
        var window = new FakeWindow { FocusedElement = Escuro, Kind = RemountFocusKind.Pointer };

        Assert.Equal(0, new ThemeRemountFocus().RemountAll(window, [() => false, () => false]));
        window.Drain();

        Assert.Empty(window.Restores);
    }

    [Fact]
    public void TheRestoreWaitsForTheLayout_NeverDuringTheRemounts()
    {
        var window = new FakeWindow { FocusedElement = Escuro, Kind = RemountFocusKind.Pointer };

        new ThemeRemountFocus().RemountAll(window, FourRootsMovingFocusTo(window, Sistema));

        Assert.Empty(window.Restores); // queued, not run inline
        window.Drain();
        Assert.Single(window.Restores);
    }

    // ---- the production port (Atlas review AT-05) ---------------------------------------------------------------------

    /// <summary>A window whose focus falls to "Sistema" whenever a root leaves the tree, seen through the one-call surface.</summary>
    private sealed class FakeFocusSurface : IFocusSurface
    {
        private readonly Queue<Action> _queue = new();

        public object? FocusedElement { get; set; }

        public Dictionary<object, RemountFocusKind> Kinds { get; } = [];

        public HashSet<object> Detached { get; } = [];

        public List<(object Element, RemountFocusKind Kind)> Focuses { get; } = [];

        public RemountFocusKind KindOf(object element) => Kinds.GetValueOrDefault(element, RemountFocusKind.Programmatic);

        public bool CanFocus(object element) => !Detached.Contains(element);

        public void Focus(object element, RemountFocusKind kind)
        {
            Focuses.Add((element, kind));
            FocusedElement = element;
            Kinds[element] = kind;
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

    private static Func<bool>[] FourRootsMovingFocusTo(FakeFocusSurface window, object fallback) =>
        Enumerable.Range(0, 4).Select(_ => (Func<bool>)(() =>
        {
            window.FocusedElement = fallback;
            window.Kinds[fallback] = RemountFocusKind.Pointer;
            return true;
        })).ToArray();

    /// <summary>The restore targets the CAPTURED element, never whatever holds focus when the restore runs.</summary>
    [Theory]
    [InlineData(RemountFocusKind.Pointer)]
    [InlineData(RemountFocusKind.Keyboard)]
    public void ThePort_RestoresTheCapturedElement_NotTheCurrentFocus(RemountFocusKind kind)
    {
        var window = new FakeFocusSurface { FocusedElement = Escuro };
        window.Kinds[Escuro] = kind;

        new ThemeRemountFocus().RemountAll(new RemountFocusPort(window), FourRootsMovingFocusTo(window, Sistema));
        Assert.Equal(Sistema, window.FocusedElement); // what the remounts left
        window.Drain();

        Assert.Equal([(Escuro, kind)], window.Focuses);
        Assert.Equal(Escuro, window.FocusedElement);
    }

    [Fact]
    public void ThePort_NeverFocusesAnElementThatLeftTheTree()
    {
        var window = new FakeFocusSurface { FocusedElement = Escuro };
        window.Kinds[Escuro] = RemountFocusKind.Pointer;
        window.Detached.Add(Escuro);

        new ThemeRemountFocus().RemountAll(new RemountFocusPort(window), FourRootsMovingFocusTo(window, Sistema));
        window.Drain();

        Assert.Empty(window.Focuses);
    }

    [Fact]
    public void ThePort_NothingFocused_NothingRestored()
    {
        var window = new FakeFocusSurface();

        new ThemeRemountFocus().RemountAll(new RemountFocusPort(window), FourRootsMovingFocusTo(window, Sistema));
        window.Drain();

        Assert.Empty(window.Focuses);
    }

    // ---- Atlas review A-2: the later restore never steals a newer choice ----------------------------------------------

    /// <summary>Atlas probe (permanent): a user focus choice after the remount and before the Low-priority restore wins.</summary>
    [Fact]
    public void AQueuedThemeRestore_DoesNotStealFocusFromALaterUserChoice()
    {
        var window = new FakeWindow { FocusedElement = Escuro, Kind = RemountFocusKind.Keyboard };
        new ThemeRemountFocus().RemountAll(window, [() => { window.FocusedElement = Sistema; return true; }]);

        window.FocusedElement = OtherControl; // the user moves on before the restore runs
        window.Drain();

        Assert.Empty(window.Restores);
        Assert.Equal(OtherControl, window.FocusedElement);
    }

    [Fact]
    public void ThePort_AQueuedRestore_DoesNotStealFocusFromALaterUserChoice()
    {
        var window = new FakeFocusSurface { FocusedElement = Escuro };
        window.Kinds[Escuro] = RemountFocusKind.Pointer;
        new ThemeRemountFocus().RemountAll(new RemountFocusPort(window), FourRootsMovingFocusTo(window, Sistema));

        window.FocusedElement = OtherControl;
        window.Drain();

        Assert.Empty(window.Focuses);
    }

    /// <summary>
    /// Two theme passes before the first restore ran (rapid theme changes): the second pass must not capture "Sistema",
    /// where the first pass dropped focus - the original choice is restored, once.
    /// </summary>
    [Fact]
    public void ASecondPass_BeforeTheRestore_KeepsTheOriginalChoice_AndRestoresOnce()
    {
        var window = new FakeWindow { FocusedElement = Escuro, Kind = RemountFocusKind.Pointer };
        var focus = new ThemeRemountFocus();

        focus.RemountAll(window, FourRootsMovingFocusTo(window, Sistema));
        focus.RemountAll(window, FourRootsMovingFocusTo(window, Sistema));
        window.Drain();

        Assert.Equal([(Escuro, RemountFocusKind.Pointer)], window.Restores);
    }

    /// <summary>After a completed restore, the next pass captures afresh (no stale pending capture).</summary>
    [Fact]
    public void APassAfterACompletedRestore_CapturesAfresh()
    {
        var window = new FakeWindow { FocusedElement = Escuro, Kind = RemountFocusKind.Pointer };
        var focus = new ThemeRemountFocus();
        focus.RemountAll(window, FourRootsMovingFocusTo(window, Sistema));
        window.Drain();

        window.FocusedElement = Claro; // the user chose another theme item
        window.Kind = RemountFocusKind.Keyboard;
        focus.RemountAll(window, FourRootsMovingFocusTo(window, Sistema));
        window.Drain();

        Assert.Equal([(Escuro, RemountFocusKind.Pointer), (Claro, RemountFocusKind.Keyboard)], window.Restores);
    }

    /// <summary>Remounts that leave NO element focused: focus is restored (nothing to override).</summary>
    [Fact]
    public void FocusLostEntirely_IsRestored()
    {
        var window = new FakeWindow { FocusedElement = Escuro, Kind = RemountFocusKind.Keyboard };
        new ThemeRemountFocus().RemountAll(window, [() => { window.FocusedElement = null; return true; }]);
        window.Drain();

        Assert.Equal([(Escuro, RemountFocusKind.Keyboard)], window.Restores);
    }

    /// <summary>The remount dropped focus on "Sistema", then focus was lost entirely before the restore: nothing to override.</summary>
    [Fact]
    public void FocusLostAfterTheFallback_IsRestored()
    {
        var window = new FakeWindow { FocusedElement = Escuro, Kind = RemountFocusKind.Pointer };
        new ThemeRemountFocus().RemountAll(window, [() => { window.FocusedElement = Sistema; return true; }]);

        window.FocusedElement = null;
        window.Drain();

        Assert.Equal([(Escuro, RemountFocusKind.Pointer)], window.Restores);
    }

    // ---- Atlas review A-2, round 3: identity, never position --------------------------------------------------------

    /// <summary>
    /// Atlas probes (permanent): after the remount dropped focus on "Sistema", the user picks "Claro" with the keyboard -
    /// the SAME group - before the restore runs; with or without a second theme pass in between, "Claro" stays.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewChoiceInTheSameGroup_BeforeTheRestore_Wins(bool anotherPass)
    {
        var window = new FakeFocusSurface { FocusedElement = Escuro };
        window.Kinds[Escuro] = RemountFocusKind.Keyboard;
        window.Kinds[Claro] = RemountFocusKind.Keyboard;
        var focus = new ThemeRemountFocus();
        var port = new RemountFocusPort(window);

        focus.RemountAll(port, [() => { window.FocusedElement = Sistema; return true; }]);
        window.FocusedElement = Claro; // an explicit keyboard choice in the same group
        if (anotherPass)
        {
            focus.RemountAll(port, [() => { window.FocusedElement = Sistema; return true; }]);
        }

        window.Drain();

        Assert.Equal(Claro, window.FocusedElement);
        Assert.DoesNotContain(window.Focuses, f => Equals(f.Element, Escuro));
    }

    /// <summary>A focus that arrives later on ANY other element - same group included - is never overridden.</summary>
    [Fact]
    public void FocusArrivingLaterOnAnotherElement_IsNeverOverridden()
    {
        var window = new FakeWindow { FocusedElement = Escuro, Kind = RemountFocusKind.Pointer };
        new ThemeRemountFocus().RemountAll(window, [() => { window.FocusedElement = null; return true; }]);

        window.FocusedElement = Sistema; // not the element our remount left focus on (that was nothing)
        window.Drain();

        Assert.Empty(window.Restores);
    }

    /// <summary>Each pass records its OWN fallback: a second pass that drops focus elsewhere still restores the original choice.</summary>
    [Fact]
    public void ASecondPass_WithAnotherFallback_StillRestoresTheOriginalChoice()
    {
        var window = new FakeWindow { FocusedElement = Escuro, Kind = RemountFocusKind.Pointer };
        var focus = new ThemeRemountFocus();

        focus.RemountAll(window, [() => { window.FocusedElement = Sistema; return true; }]);
        focus.RemountAll(window, [() => { window.FocusedElement = OtherControl; return true; }]);
        window.Drain();

        Assert.Equal([(Escuro, RemountFocusKind.Pointer)], window.Restores);
    }

    /// <summary>A new choice followed by a pass whose remounts change nothing: the earlier restore does not override it.</summary>
    [Fact]
    public void ANewChoice_ThenAPassWithoutRemount_KeepsTheChoice()
    {
        var window = new FakeWindow { FocusedElement = Escuro, Kind = RemountFocusKind.Pointer };
        var focus = new ThemeRemountFocus();
        focus.RemountAll(window, [() => { window.FocusedElement = Sistema; return true; }]);

        window.FocusedElement = Claro;
        focus.RemountAll(window, [() => false]);
        window.Drain();

        Assert.Empty(window.Restores);
    }
}
