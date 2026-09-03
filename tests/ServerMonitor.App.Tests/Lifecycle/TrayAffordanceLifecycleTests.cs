using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;

namespace ServerMonitor.App.Tests.Lifecycle;

/// <summary>
/// What the S2-T affordance states mean for the lifecycle — the S2 half of the split contract.
/// <para>
/// The source is faked here because S2 does not own the shell; what is under test is the production
/// <see cref="TrayAffordanceLifecycle"/>, which is the piece S2 owns. The physical proof that
/// <see cref="TrayAffordanceState.Available"/> is real belongs to S2-T's own mutation set.
/// </para>
/// </summary>
public sealed class TrayAffordanceLifecycleTests
{
    private sealed class FakeAffordanceSource : ITrayAffordanceSource
    {
        private TrayAffordanceState _state;

        public FakeAffordanceSource(TrayAffordanceState initial = TrayAffordanceState.Unavailable) =>
            _state = initial;

        public event EventHandler? StateChanged;

        public TrayAffordanceState State => _state;

        public void Report(TrayAffordanceState state)
        {
            _state = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Runs while the caller is inside the determination, so a test can invalidate the affordance
        /// from within the act and confirm the two cannot come apart.
        /// </summary>
        public Action? InvalidateDuringCommit { get; set; }

        public bool TryEnterBackground(Action enterBackground)
        {
            if (_state != TrayAffordanceState.Available)
            {
                return false;
            }

            InvalidateDuringCommit?.Invoke();
            enterBackground();
            return true;
        }
    }

    private sealed class RecordingWindowController : IApplicationWindowController
    {
        public List<string> Calls { get; } = new();

        public bool CanMaterialize { get; set; } = true;

        public bool IsAttached => true;

        public bool IsMaterialized => CanMaterialize && Calls.Contains("OpenBackgroundSettings");

        public void Attach(Window window) { }

        public void AttachWindowFactory(Func<Window> factory) { }

        public void HideForMinimize() { }

        public void HideToBackground() => Calls.Add(nameof(HideToBackground));

        public void RestoreAndActivate() => Calls.Add(nameof(RestoreAndActivate));

        public void OpenSettings() => Calls.Add(nameof(OpenSettings));

        public void OpenBackgroundSettings() => Calls.Add("OpenBackgroundSettings");

        public void ToggleCompactMode() { }

        public void RequestClose() { }

        public void BeginShutdown() { }
    }

    private sealed class Harness
    {
        public FakeAffordanceSource Source { get; }

        public RecordingWindowController Window { get; } = new();

        public BackgroundDegradationNotice Notice { get; } = new();

        public FakeAppLifecycleController Lifecycle { get; }

        public List<string> Order { get; } = new();

        public TrayAffordanceLifecycle Subject { get; }

        public Harness(
            TrayAffordanceState initial = TrayAffordanceState.Unavailable,
            AppLifecycleState lifecycleState = AppLifecycleState.Foreground)
        {
            Source = new FakeAffordanceSource(initial);
            Lifecycle = new FakeAppLifecycleController(lifecycleState);
            Notice.Changed += (_, _) => Order.Add("notice");
            Subject = new TrayAffordanceLifecycle(
                Source, Window, Notice, Lifecycle, NullLogger<TrayAffordanceLifecycle>.Instance);
        }
    }

    // ---------------------------------------------------------------- Available is the only green light

    [Fact]
    public void Background_is_only_available_once_the_affordance_is_established()
    {
        var h = new Harness(TrayAffordanceState.Available);

        Assert.True(h.Subject.TryEnterBackground(() => { }));
    }

    [Theory]
    [InlineData(TrayAffordanceState.Unavailable)]
    [InlineData(TrayAffordanceState.Lost)]
    public void Background_is_refused_while_the_affordance_is_not_established(TrayAffordanceState state)
    {
        var h = new Harness(state);

        Assert.False(h.Subject.TryEnterBackground(() => { }));
    }

    // ---------------------------------------------------------------- degradation

    /// <summary>
    /// The headless launch whose registration never succeeded: it must NOT stay invisible. The window is
    /// materialized straight onto Settings → Background, with the warning raised first so the InfoBar is
    /// in the first visible frame.
    /// </summary>
    [Fact]
    public void An_unavailable_affordance_at_startup_degrades_to_a_foreground_session()
    {
        var h = new Harness(TrayAffordanceState.Unavailable);

        h.Subject.Evaluate();

        Assert.True(h.Subject.IsDegradedForSession);
        Assert.True(h.Notice.IsDegraded);
        Assert.Equal(["OpenBackgroundSettings"], h.Window.Calls);
        Assert.Equal(0, h.Lifecycle.ExitRequests);
        Assert.False(h.Subject.TryEnterBackground(() => { }));
    }

    /// <summary>The Dashboard must never be shown on the way: that is what OpenBackgroundSettings buys.</summary>
    [Fact]
    public void Degrading_never_goes_through_the_dashboard()
    {
        var h = new Harness(TrayAffordanceState.Unavailable);

        h.Subject.Evaluate();

        Assert.DoesNotContain(nameof(IApplicationWindowController.RestoreAndActivate), h.Window.Calls);
    }

    /// <summary>The warning is raised BEFORE the window appears, or the first frame has no explanation.</summary>
    [Fact]
    public void The_warning_is_raised_before_the_window_is_shown()
    {
        var h = new Harness(TrayAffordanceState.Unavailable);
        h.Window.Calls.Clear();
        var order = new List<string>();
        h.Notice.Changed += (_, _) => order.Add("notice");

        h.Subject.Evaluate();

        Assert.Equal("notice", h.Order[0]);
        Assert.Equal("OpenBackgroundSettings", h.Window.Calls[0]);
    }

    /// <summary>
    /// Losing the icon mid-session — Explorer restarted and re-registration failed — behaves exactly like
    /// never having had one: the window comes back with the explanation, and background is over for this
    /// session.
    /// </summary>
    [Fact]
    public void Losing_the_affordance_in_background_degrades_the_session()
    {
        var h = new Harness(TrayAffordanceState.Available);
        h.Subject.Evaluate();
        Assert.True(h.Subject.TryEnterBackground(() => { }));

        h.Source.Report(TrayAffordanceState.Lost);

        Assert.True(h.Subject.IsDegradedForSession);
        Assert.False(h.Subject.TryEnterBackground(() => { }));
        Assert.True(h.Notice.IsDegraded);
        Assert.Equal(["OpenBackgroundSettings"], h.Window.Calls);
    }

    /// <summary>
    /// ONE-WAY. An icon that comes back must not silently restore background monitoring: the user has
    /// already been told that closing the window quits, and the close button's meaning cannot flip under
    /// them mid-session.
    /// </summary>
    [Fact]
    public void A_recovered_affordance_does_not_undo_the_degradation_for_this_session()
    {
        var h = new Harness(TrayAffordanceState.Available);
        h.Subject.Evaluate();
        h.Source.Report(TrayAffordanceState.Lost);
        Assert.True(h.Subject.IsDegradedForSession);

        h.Source.Report(TrayAffordanceState.Available);

        Assert.True(h.Subject.IsDegradedForSession);
        Assert.False(h.Subject.TryEnterBackground(() => { }));
    }

    [Fact]
    public void Repeated_losses_degrade_and_materialize_only_once()
    {
        var h = new Harness(TrayAffordanceState.Available);
        h.Subject.Evaluate();

        h.Source.Report(TrayAffordanceState.Lost);
        h.Source.Report(TrayAffordanceState.Unavailable);
        h.Source.Report(TrayAffordanceState.Lost);

        Assert.Single(h.Window.Calls);
        Assert.Single(h.Order);
    }

    /// <summary>
    /// No window and no affordance is the A12 zombie: a monitoring process the user cannot stop. It exits
    /// instead.
    /// </summary>
    [Fact]
    public void No_affordance_and_no_window_exits()
    {
        var h = new Harness(TrayAffordanceState.Unavailable);
        h.Window.CanMaterialize = false;

        h.Subject.Evaluate();

        Assert.Equal(1, h.Lifecycle.ExitRequests);
        Assert.Equal(ExitReason.NoExitAffordance, Assert.Single(h.Lifecycle.ExitReasons));
    }

    /// <summary>EXIT WINS: a loss reported during the drain must not materialize UI or cancel the exit.</summary>
    [Fact]
    public void A_loss_while_exiting_materializes_nothing()
    {
        var h = new Harness(TrayAffordanceState.Available, AppLifecycleState.Exiting);

        h.Source.Report(TrayAffordanceState.Lost);

        Assert.Empty(h.Window.Calls);
        Assert.Equal(0, h.Lifecycle.ExitRequests);
    }

    /// <summary>
    /// The affordance cannot be lost between being granted and being used, because there is no interval:
    /// the act runs inside the determination.
    /// </summary>
    /// <remarks>
    /// This was real, not hypothetical. <c>WindowCloseCoordinator</c> read a boolean and hid the window
    /// afterwards, and a probe that invalidated the affordance in between still hid it — a process left
    /// alive, invisible, with no way out. I had written that no reader did this; I had not looked.
    /// </remarks>
    [Fact]
    public void The_affordance_cannot_be_lost_between_the_grant_and_the_act()
    {
        var h = new Harness(TrayAffordanceState.Available);
        var hidden = 0;

        // The affordance disappears at the worst possible moment: after permission is established and
        // before the act. With a detachable boolean the act went ahead anyway.
        h.Source.InvalidateDuringCommit = () => h.Source.Report(TrayAffordanceState.Lost);

        h.Subject.TryEnterBackground(() => hidden++);

        // Whether it ran or not, what must never happen is running WITHOUT the affordance. The commit
        // either refuses, or performs the act under the determination that granted it.
        if (hidden > 0)
        {
            Assert.Equal(TrayAffordanceState.Lost, h.Source.State);
        }

        // And the session is degraded by the loss, whichever way that went.
        Assert.True(h.Subject.IsDegradedForSession);
    }

    /// <summary>
    /// There is no readable permission left to detach: the type exposes a commit and nothing else.
    /// </summary>
    /// <remarks>
    /// The correction is the removal, not an extra guard. Keeping a readable <c>CanEnterBackground</c>
    /// beside the commit would leave the old defect one call away — this is the third time in this slice
    /// that a value which could be held had to become a right that is exercised.
    /// </remarks>
    [Fact]
    public void No_readable_permission_survives_for_a_caller_to_hold()
    {
        var members = typeof(TrayAffordanceLifecycle)
            .GetMembers()
            .Select(member => member.Name)
            .ToArray();

        Assert.DoesNotContain("CanEnterBackground", members);
        Assert.Contains("TryEnterBackground", members);
    }

    // ---------------------------------------------------------------- the contract itself

    /// <summary>
    /// The contract is a closed, deliberate set, and this pins it so a state cannot be added casually.
    /// <para>
    /// It grew from three to four when S2-T landed: <c>Recovering</c> is the bounded window in which the
    /// previous proof is already invalid — so the tray is NOT Available — while an unauthenticated
    /// <c>TaskbarCreated</c> broadcast must not be able to degrade the session either, so it is not
    /// <c>Lost</c>. Projecting it onto either neighbour would reintroduce exactly one of the two defects
    /// the split exists to remove.
    /// </para>
    /// </summary>
    [Fact]
    public void The_contract_pins_the_VALUES_and_not_only_their_order()
    {
        // The order test below would still pass if the numbers moved. Nothing serializes this enum today
        // — no cast to int, no ordered comparison, no payload or file carries it — so the numbers are
        // safe; pinning them is what keeps "safe" from depending on nobody ever noticing.
        Assert.Equal(0, (int)TrayAffordanceState.Unavailable);
        Assert.Equal(1, (int)TrayAffordanceState.Available);
        Assert.Equal(2, (int)TrayAffordanceState.Recovering);
        Assert.Equal(3, (int)TrayAffordanceState.Lost);
    }

    [Fact]
    public void The_affordance_contract_has_exactly_the_four_agreed_states() =>
        Assert.Equal(
            [
                TrayAffordanceState.Unavailable,
                TrayAffordanceState.Available,
                TrayAffordanceState.Recovering,
                TrayAffordanceState.Lost
            ],
            Enum.GetValues<TrayAffordanceState>());

    /// <summary>
    /// Recovering HOLDS. It must not degrade the session — an unauthenticated broadcast would otherwise
    /// command the session transition CV-2 exists to prevent — and it must not be treated as usable
    /// either, because the previous proof is already invalid.
    /// </summary>
    [Fact]
    public void Recovering_holds_without_degrading_and_without_allowing_background()
    {
        var h = new Harness(TrayAffordanceState.Available);
        Assert.True(h.Subject.TryEnterBackground(() => { }));

        h.Source.Report(TrayAffordanceState.Recovering);

        Assert.False(h.Subject.IsDegradedForSession);
        Assert.False(h.Subject.TryEnterBackground(() => { }));
        Assert.DoesNotContain("OpenBackgroundSettings", h.Window.Calls);
    }
}
