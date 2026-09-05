using ServerMonitor.App.Shell.Tray;

namespace ServerMonitor.App.Tests.Shell;

/// <summary>
/// CV-9. A flyout is already open; another <c>WM_CONTEXTMENU</c> arrives — forged or not, the shell
/// cannot tell us and neither can we. It must produce nothing.
/// </summary>
public sealed class FlyoutReentrancyGateTests
{
    [Fact]
    public void The_first_request_opens_the_only_flyout()
    {
        var gate = new FlyoutReentrancyGate();

        Assert.True(gate.TryOpen());
        Assert.True(gate.IsOpen);
    }

    [Fact]
    public void A_second_request_while_one_is_open_is_refused()
    {
        var gate = new FlyoutReentrancyGate();
        gate.TryOpen();

        Assert.False(gate.TryOpen());
    }

    [Fact]
    public void A_flood_of_requests_yields_exactly_one_open()
    {
        // The threat model is a repeatable message from a local process, not a user double-clicking, so
        // the assertion is on the COUNT of admissions rather than on the second one alone.
        var gate = new FlyoutReentrancyGate();
        var opened = 0;

        for (var i = 0; i < 200; i++)
        {
            if (gate.TryOpen())
            {
                opened++;
            }
        }

        Assert.Equal(1, opened);
    }

    [Fact]
    public void The_slot_is_reusable_after_the_flyout_closes()
    {
        // Refusing forever would be fail-closed in the wrong place: the menu is the exit affordance.
        var gate = new FlyoutReentrancyGate();
        gate.TryOpen();
        gate.Close();

        Assert.True(gate.TryOpen());
    }

    [Fact]
    public void Closing_twice_does_not_release_a_second_slot()
    {
        // A dismissal and a programmatic close can both arrive. If the second Close let a request
        // through, a hostile sender that timed itself against a real dismissal would get its flyout.
        var gate = new FlyoutReentrancyGate();
        gate.TryOpen();
        gate.Close();
        gate.Close();

        Assert.True(gate.TryOpen());
        Assert.False(gate.TryOpen());
    }

    [Fact]
    public async Task Concurrent_requests_still_yield_exactly_one_open()
    {
        var gate = new FlyoutReentrancyGate();
        var opened = 0;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            if (gate.TryOpen())
            {
                Interlocked.Increment(ref opened);
            }
        })));

        Assert.Equal(1, Volatile.Read(ref opened));
    }
}
