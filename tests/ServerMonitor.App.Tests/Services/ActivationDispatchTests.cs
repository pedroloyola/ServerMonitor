using ServerMonitor.ActivationContract;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// The "exactly one restore per logical activation" invariant (M13-QA-10 defensive fix B), counted end to
/// end over the REAL activation machinery: <see cref="ActivationDispatch"/> in front of a real
/// <see cref="PendingActivation"/> and a real <see cref="ActivationRouter"/>, with the router's executor
/// standing in for <c>App.ExecuteActivationIntent</c> — which, in the app, restores the window before it
/// navigates. Both paths increment ONE counter, so a regression that restores twice fails on the number.
/// <para>
/// Before the fix the redirect handler delivered the intent AND restored unconditionally, so a widget
/// deep-link produced two restores for one click; the first test here is that regression.
/// </para>
/// </summary>
public sealed class ActivationDispatchTests
{
    /// <summary>
    /// Counts window restores, whichever path asks for one. It is the stand-in for
    /// <c>IApplicationWindowController.RestoreAndActivate</c>, which is what both paths ultimately call.
    /// </summary>
    private sealed class RestoreCounter
    {
        public int Total { get; private set; }

        public int FromIntentPath { get; private set; }

        public int FromRedirectPath { get; private set; }

        public void ByIntent()
        {
            Total++;
            FromIntentPath++;
        }

        public void ByRedirect()
        {
            Total++;
            FromRedirectPath++;
        }
    }

    private sealed class Harness
    {
        public RestoreCounter Restores { get; } = new();

        public PendingActivation Pending { get; } = new();

        public ActivationRouter Router { get; }

        public ActivationDispatch Dispatch { get; }

        public List<ActivationIntent> Executed { get; } = new();

        public Harness(bool shellReady = true)
        {
            Router = new ActivationRouter(intent =>
            {
                // What App.ExecuteActivationIntent does: surface the window, then navigate.
                Restores.ByIntent();
                Executed.Add(intent);
            });

            Pending.Attach(Router.Route);
            if (shellReady)
            {
                Router.MarkReady();
            }

            Dispatch = new ActivationDispatch(
                intent => Pending.Deliver(intent),
                () => Restores.ByRedirect());
        }
    }

    private static ActivationIntent Server() =>
        ActivationIntent.Server(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public void A_deep_link_activation_restores_the_window_exactly_once()
    {
        var h = new Harness();

        h.Dispatch.Dispatch(ActivationIntent.Dashboard);

        Assert.Equal(1, h.Restores.Total);
        Assert.Equal(1, h.Restores.FromIntentPath);
        Assert.Equal(0, h.Restores.FromRedirectPath); // the regression: this used to be 1 as well
        Assert.Single(h.Executed);
    }

    [Fact]
    public void A_server_deep_link_activation_restores_the_window_exactly_once()
    {
        var h = new Harness();

        h.Dispatch.Dispatch(Server());

        Assert.Equal(1, h.Restores.Total);
        Assert.Equal(0, h.Restores.FromRedirectPath);
    }

    /// <summary>
    /// A plain second launch (or a notification click) carries no intent, so nothing else would restore
    /// the window — the redirect path must still do it, exactly once.
    /// </summary>
    [Fact]
    public void An_activation_without_a_deep_link_still_restores_the_window_exactly_once()
    {
        var h = new Harness();

        h.Dispatch.Dispatch(null);

        Assert.Equal(1, h.Restores.Total);
        Assert.Equal(1, h.Restores.FromRedirectPath);
        Assert.Empty(h.Executed);
    }

    [Fact]
    public void Every_activation_costs_exactly_one_restore_whatever_the_mix()
    {
        var h = new Harness();

        h.Dispatch.Dispatch(ActivationIntent.Dashboard);
        h.Dispatch.Dispatch(null);
        h.Dispatch.Dispatch(Server());
        h.Dispatch.Dispatch(null);
        h.Dispatch.Dispatch(ActivationIntent.Dashboard);

        Assert.Equal(5, h.Restores.Total);
        Assert.Equal(3, h.Restores.FromIntentPath);
        Assert.Equal(2, h.Restores.FromRedirectPath);
    }

    /// <summary>
    /// Activations that arrive before the shell is ready must not restore anything yet — and must not
    /// "save up" restores either: the router coalesces to the latest intent (§28), so readiness produces
    /// ONE restore, not one per buffered activation.
    /// </summary>
    [Fact]
    public void Activations_before_the_shell_is_ready_produce_one_restore_when_it_becomes_ready()
    {
        var h = new Harness(shellReady: false);

        h.Dispatch.Dispatch(ActivationIntent.Dashboard);
        h.Dispatch.Dispatch(Server());
        Assert.Equal(0, h.Restores.Total);

        h.Router.MarkReady();

        Assert.Equal(1, h.Restores.Total);
        Assert.Equal(ActivationIntentKind.OpenServer, Assert.Single(h.Executed).Kind); // latest wins
    }

    /// <summary>
    /// The dispatch order is load-bearing: the intent is handed over BEFORE any restore, so a newer
    /// activation is never overtaken by the restore that an older one triggered.
    /// </summary>
    [Fact]
    public void The_intent_is_delivered_before_any_restore_runs()
    {
        var order = new List<string>();
        var dispatch = new ActivationDispatch(
            _ => order.Add("deliver"),
            () => order.Add("restore"));

        dispatch.Dispatch(null);

        Assert.Equal(["deliver", "restore"], order);
    }

    [Fact]
    public void Both_callbacks_are_required()
    {
        Assert.Throws<ArgumentNullException>(() => new ActivationDispatch(null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => new ActivationDispatch(_ => { }, null!));
    }
}
