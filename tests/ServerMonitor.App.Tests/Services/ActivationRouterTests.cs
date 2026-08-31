using ServerMonitor.ActivationContract;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Services;

public sealed class ActivationRouterTests
{
    private static readonly Guid Id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Intent_before_ready_is_buffered_and_runs_once_on_ready()
    {
        var runs = new List<ActivationIntent>();
        var router = new ActivationRouter(runs.Add);

        router.Route(ActivationIntent.Server(Id1));
        Assert.Empty(runs); // shell not ready yet

        router.MarkReady();
        Assert.Equal(new[] { ActivationIntent.Server(Id1) }, runs);
    }

    [Fact]
    public void Intent_after_ready_runs_immediately()
    {
        var runs = new List<ActivationIntent>();
        var router = new ActivationRouter(runs.Add);
        router.MarkReady();

        router.Route(ActivationIntent.Dashboard);
        Assert.Equal(new[] { ActivationIntent.Dashboard }, runs);
    }

    [Fact]
    public void Rapid_intents_before_ready_coalesce_to_the_last()
    {
        var runs = new List<ActivationIntent>();
        var router = new ActivationRouter(runs.Add);

        router.Route(ActivationIntent.Server(Id1));
        router.Route(ActivationIntent.Server(Id1));
        router.Route(ActivationIntent.Server(Id2)); // last wins (§28)

        router.MarkReady();
        Assert.Equal(new[] { ActivationIntent.Server(Id2) }, runs);
    }

    [Fact]
    public void Null_intent_is_ignored()
    {
        var runs = new List<ActivationIntent>();
        var router = new ActivationRouter(runs.Add);
        router.MarkReady();

        router.Route(null);
        Assert.Empty(runs);
    }

    [Fact]
    public void Mark_ready_is_idempotent_and_flushes_nothing_twice()
    {
        var runs = new List<ActivationIntent>();
        var router = new ActivationRouter(runs.Add);

        router.Route(ActivationIntent.Dashboard);
        router.MarkReady();
        router.MarkReady(); // no double-run

        Assert.Single(runs);
    }

    [Fact]
    public void Ready_with_no_pending_runs_nothing()
    {
        var runs = new List<ActivationIntent>();
        var router = new ActivationRouter(runs.Add);
        router.MarkReady();
        Assert.Empty(runs);
    }

    [Fact]
    public async Task An_intent_arriving_during_execution_is_ordered_after_and_wins_last()
    {
        // M-1: while the executor is running intent A, a concurrent Route(B) must not overtake or run
        // concurrently — it is picked up by the single drain after A, and B is the final state.
        var runs = new List<ActivationIntent>();
        var entered = new SemaphoreSlim(0);
        var release = new ManualResetEventSlim(false);
        var first = true;

        var router = new ActivationRouter(intent =>
        {
            var isFirst = first;
            first = false;
            runs.Add(intent);
            if (isFirst)
            {
                entered.Release();  // signal we are inside the first execution
                release.Wait();     // hold the drain here
            }
        });

        router.MarkReady();
        var route = Task.Run(() => router.Route(ActivationIntent.Dashboard)); // A, blocks in executor
        Assert.True(await entered.WaitAsync(5000));                           // A is executing

        router.Route(ActivationIntent.Server(Id1)); // B arrives during A's execution → buffered, not run yet
        Assert.Single(runs);                         // only A has run so far (no overtaking)

        release.Set();                               // let A finish → the drain picks up B
        await route;

        Assert.Equal(new[] { ActivationIntent.Dashboard, ActivationIntent.Server(Id1) }, runs); // A then B
    }

    [Fact]
    public void A_throwing_executor_does_not_wedge_the_drain_owner()
    {
        // L-1: if the executor throws, the drain owner must not leave _draining stuck true (which would
        // silently drop every later activation). The failure is reported to the error sink and the router
        // stays usable for the next intent.
        var errors = new List<Exception>();
        var throwFor = ActivationIntent.Server(Id1);
        var runs = new List<ActivationIntent>();
        var router = new ActivationRouter(
            intent =>
            {
                if (intent == throwFor)
                {
                    throw new InvalidOperationException("boom");
                }

                runs.Add(intent);
            },
            errors.Add);

        router.MarkReady();

        router.Route(throwFor);                 // throws inside the drain
        Assert.Single(errors);                  // reported, not swallowed silently
        Assert.Empty(runs);

        router.Route(ActivationIntent.Dashboard); // router is NOT wedged: this still runs
        Assert.Equal(new[] { ActivationIntent.Dashboard }, runs);
    }

    [Fact]
    public void A_throwing_executor_AND_a_throwing_error_sink_still_do_not_wedge_the_router()
    {
        // L-1 (hardened): even if BOTH the executor and the error sink throw, the drain owner must be
        // released so later activations still run. A failing sink must never freeze routing.
        var throwFor = ActivationIntent.Server(Id1);
        var runs = new List<ActivationIntent>();
        var router = new ActivationRouter(
            intent =>
            {
                if (intent == throwFor)
                {
                    throw new InvalidOperationException("executor boom");
                }

                runs.Add(intent);
            },
            _ => throw new InvalidOperationException("sink boom"));

        router.MarkReady();

        router.Route(throwFor); // executor throws, sink throws — neither may wedge the drain
        Assert.Empty(runs);

        router.Route(ActivationIntent.Dashboard); // router is NOT wedged
        Assert.Equal(new[] { ActivationIntent.Dashboard }, runs);
    }
}
