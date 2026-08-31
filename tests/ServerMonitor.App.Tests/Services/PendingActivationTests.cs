using ServerMonitor.ActivationContract;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Tests.Services;

public sealed class PendingActivationTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Buffered_intent_is_flushed_to_the_consumer_on_attach()
    {
        var pending = new PendingActivation();
        var delivered = new List<ActivationIntent>();

        pending.Deliver(ActivationIntent.Server(Id)); // no consumer yet → buffered
        Assert.Empty(delivered);

        pending.Attach(delivered.Add); // atomically flushes the buffered intent
        Assert.Equal(new[] { ActivationIntent.Server(Id) }, delivered);
    }

    [Fact]
    public void Intent_after_attach_is_delivered_immediately_not_buffered()
    {
        var pending = new PendingActivation();
        var delivered = new List<ActivationIntent>();
        pending.Attach(delivered.Add);

        pending.Deliver(ActivationIntent.Dashboard);
        Assert.Equal(new[] { ActivationIntent.Dashboard }, delivered);
    }

    [Fact]
    public void Latest_buffered_intent_wins_at_flush()
    {
        var pending = new PendingActivation();
        var delivered = new List<ActivationIntent>();

        pending.Deliver(ActivationIntent.Server(Id));
        pending.Deliver(ActivationIntent.Dashboard); // last wins
        pending.Attach(delivered.Add);

        Assert.Equal(new[] { ActivationIntent.Dashboard }, delivered);
    }

    [Fact]
    public void Null_deliver_does_not_clobber_a_real_pending_intent()
    {
        var pending = new PendingActivation();
        var delivered = new List<ActivationIntent>();

        pending.Deliver(ActivationIntent.Dashboard);
        pending.Deliver(null); // a non-deep-link activation must not erase the real one
        pending.Attach(delivered.Add);

        Assert.Equal(new[] { ActivationIntent.Dashboard }, delivered);
    }

    [Fact]
    public void Attach_with_no_buffered_intent_flushes_nothing()
    {
        var pending = new PendingActivation();
        var delivered = new List<ActivationIntent>();

        pending.Attach(delivered.Add);
        Assert.Empty(delivered);
    }

    [Fact]
    public void Null_deliver_after_attach_is_ignored()
    {
        var pending = new PendingActivation();
        var delivered = new List<ActivationIntent>();
        pending.Attach(delivered.Add);

        pending.Deliver(null);
        Assert.Empty(delivered);
    }

    [Fact]
    public async Task A_redirect_racing_attach_is_not_overtaken_by_the_older_buffered_intent()
    {
        // M-1: cold A is buffered; while Attach is flushing A through the consumer, redirect B (a newer
        // user action) arrives via Deliver. The single-owner drain must deliver A then B — the newer B is
        // never overtaken by the older A at the construction boundary.
        var delivered = new List<ActivationIntent>();
        var entered = new SemaphoreSlim(0);
        var release = new ManualResetEventSlim(false);
        var first = true;

        var pending = new PendingActivation();
        pending.Deliver(ActivationIntent.Dashboard); // cold A, buffered (no consumer yet)

        var attach = Task.Run(() => pending.Attach(intent =>
        {
            var isFirst = first;
            first = false;
            delivered.Add(intent);
            if (isFirst)
            {
                entered.Release(); // we are inside the flush of A
                release.Wait();    // hold the drain here
            }
        }));

        Assert.True(await entered.WaitAsync(5000)); // A is being delivered
        pending.Deliver(ActivationIntent.Server(Id)); // B arrives during A's delivery → buffered, not run
        Assert.Single(delivered);                     // only A so far (no overtaking)

        release.Set();                                // let A finish → the drain picks up B
        await attach;

        Assert.Equal(new[] { ActivationIntent.Dashboard, ActivationIntent.Server(Id) }, delivered); // A then B
    }
}
