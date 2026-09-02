using Microsoft.Extensions.Time.Testing;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Tests.Fakes;

namespace ServerMonitor.WidgetProvider.Tests;

/// <summary>
/// The repaint pump's own behavior: debounce, coalescing, the lost-event backstop, and deterministic
/// teardown. Everything is driven by <see cref="FakeTimeProvider"/> and an explicit change source, so
/// there is no wall-clock dependency and nothing can flake.
/// </summary>
public sealed class WidgetSnapshotChangeWatcherTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(60);

    private sealed class Harness : IDisposable
    {
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        public FakeSnapshotChangeSource Source { get; } = new();
        public int Refreshes;
        public Func<bool>? RefreshFails { get; set; }
        public WidgetSnapshotChangeWatcher Pump { get; }

        public Harness()
        {
            Pump = new WidgetSnapshotChangeWatcher(
                () =>
                {
                    Interlocked.Increment(ref Refreshes);
                    if (RefreshFails?.Invoke() == true)
                    {
                        throw new InvalidOperationException("repaint failed");
                    }
                },
                Source,
                Clock,
                Debounce,
                Backstop);
        }

        public void Dispose() => Pump.Dispose();
    }

    [Fact]
    public void A_signal_while_disarmed_never_repaints()
    {
        using var h = new Harness();

        h.Source.Raise();
        h.Clock.Advance(Backstop * 3);

        Assert.Equal(0, h.Refreshes);
        Assert.False(h.Pump.IsArmed);
    }

    [Fact]
    public void Arming_starts_the_change_source()
    {
        using var h = new Harness();

        h.Pump.Arm();

        Assert.True(h.Pump.IsArmed);
        Assert.Equal(1, h.Source.StartCount);
    }

    [Fact]
    public void Arming_twice_starts_the_source_once()
    {
        using var h = new Harness();

        h.Pump.Arm();
        h.Pump.Arm();

        Assert.Equal(1, h.Source.StartCount);
    }

    [Fact]
    public void A_signal_repaints_only_after_the_debounce_window_closes()
    {
        using var h = new Harness();
        h.Pump.Arm();

        h.Source.Raise();
        h.Clock.Advance(Debounce - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, h.Refreshes);

        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, h.Refreshes);
    }

    /// <summary>
    /// One atomic commit legitimately produces several filesystem events (temp created, temp renamed onto
    /// the destination, destination renamed to the backup, backup deleted). They must collapse into ONE
    /// logical repaint.
    /// </summary>
    [Fact]
    public void One_atomic_commits_burst_of_events_yields_exactly_one_repaint()
    {
        using var h = new Harness();
        h.Pump.Arm();

        h.Source.RaiseBurst(8);
        h.Clock.Advance(Debounce);

        Assert.Equal(1, h.Refreshes);
    }

    [Fact]
    public void A_continuous_stream_of_events_cannot_starve_the_repaint()
    {
        using var h = new Harness();
        h.Pump.Arm();

        // The window is never restarted by later signals, so it closes on schedule even under a stream.
        for (var i = 0; i < 10; i++)
        {
            h.Source.Raise();
            h.Clock.Advance(Debounce / 4);
        }

        Assert.True(h.Refreshes >= 2, $"expected repeated repaints under a stream, saw {h.Refreshes}");
    }

    [Fact]
    public void Two_separated_commits_yield_two_repaints()
    {
        using var h = new Harness();
        h.Pump.Arm();

        h.Source.RaiseBurst(3);
        h.Clock.Advance(Debounce);
        h.Source.RaiseBurst(3);
        h.Clock.Advance(Debounce);

        Assert.Equal(2, h.Refreshes);
    }

    /// <summary>Events can be lost outright (internal-buffer overflow): the backstop must still converge.</summary>
    [Fact]
    public void With_no_events_at_all_the_backstop_repaints_once_per_interval()
    {
        using var h = new Harness();
        h.Pump.Arm();

        h.Clock.Advance(Backstop);
        Assert.Equal(1, h.Refreshes);

        h.Clock.Advance(Backstop);
        Assert.Equal(2, h.Refreshes);
    }

    [Fact]
    public void The_backstop_is_pushed_out_by_an_event_driven_repaint()
    {
        using var h = new Harness();
        h.Pump.Arm();

        h.Source.Raise();
        h.Clock.Advance(Debounce);
        Assert.Equal(1, h.Refreshes);

        // A full interval measured from the LAST repaint, not from arming.
        h.Clock.Advance(Backstop - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, h.Refreshes);

        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, h.Refreshes);
    }

    [Fact]
    public void The_backstop_reestablishes_a_watch_that_never_started()
    {
        using var h = new Harness();
        h.Source.WatchEstablishes = false; // e.g. the snapshot directory does not exist yet
        h.Pump.Arm();
        Assert.False(h.Source.IsWatching);

        h.Source.WatchEstablishes = true;
        h.Clock.Advance(Backstop);

        Assert.Equal(2, h.Source.StartCount);
        Assert.True(h.Source.IsWatching);
        Assert.Equal(1, h.Refreshes);
    }

    [Fact]
    public void The_backstop_does_not_restart_a_healthy_watch()
    {
        using var h = new Harness();
        h.Pump.Arm();

        h.Clock.Advance(Backstop * 3);

        Assert.Equal(1, h.Source.StartCount);
    }

    [Fact]
    public void Disarming_stops_the_source_and_silences_events_and_the_backstop()
    {
        using var h = new Harness();
        h.Pump.Arm();

        h.Pump.Disarm();
        h.Source.RaiseBurst(5);
        h.Clock.Advance(Backstop * 3);

        Assert.False(h.Pump.IsArmed);
        Assert.Equal(1, h.Source.StopCount);
        Assert.Equal(0, h.Refreshes);
    }

    [Fact]
    public void Disarming_cancels_a_repaint_already_scheduled_inside_the_window()
    {
        using var h = new Harness();
        h.Pump.Arm();
        h.Source.Raise();

        h.Pump.Disarm();
        h.Clock.Advance(Debounce * 4);

        Assert.Equal(0, h.Refreshes);
    }

    [Fact]
    public void Rearming_after_a_disarm_works()
    {
        using var h = new Harness();
        h.Pump.Arm();
        h.Pump.Disarm();

        h.Pump.Arm();
        h.Source.Raise();
        h.Clock.Advance(Debounce);

        Assert.Equal(1, h.Refreshes);
        Assert.Equal(2, h.Source.StartCount);
    }

    [Fact]
    public void Disposal_is_deterministic_no_timer_or_source_callback_survives_it()
    {
        var h = new Harness();
        h.Pump.Arm();
        h.Source.Raise();

        h.Pump.Dispose();

        Assert.Equal(1, h.Source.DisposeCount);
        Assert.False(h.Source.HasSubscribers);

        h.Source.Raise();
        h.Clock.Advance(Backstop * 5);
        Assert.Equal(0, h.Refreshes);
        Assert.False(h.Pump.IsArmed);
    }

    [Fact]
    public void Disposal_is_idempotent()
    {
        var h = new Harness();
        h.Pump.Arm();

        h.Pump.Dispose();
        h.Pump.Dispose();

        Assert.Equal(1, h.Source.DisposeCount);
    }

    [Fact]
    public void Arming_after_disposal_does_nothing()
    {
        var h = new Harness();
        h.Pump.Dispose();

        h.Pump.Arm();
        h.Source.Raise();
        h.Clock.Advance(Backstop * 2);

        Assert.False(h.Pump.IsArmed);
        Assert.Equal(0, h.Refreshes);
    }

    [Fact]
    public void A_failing_repaint_never_kills_the_pump()
    {
        using var h = new Harness();
        var fail = true;
        h.RefreshFails = () => fail;
        h.Pump.Arm();

        h.Source.Raise();
        h.Clock.Advance(Debounce);
        Assert.Equal(1, h.Refreshes);

        fail = false;
        h.Source.Raise();
        h.Clock.Advance(Debounce);

        Assert.Equal(2, h.Refreshes);
    }

    /// <summary>
    /// The snapshot is replaced again while the previous version is still being painted. That signal must
    /// not be swallowed: the next window closes on it and the newest bytes reach the board.
    /// </summary>
    [Fact]
    public void A_signal_arriving_during_a_repaint_is_not_lost()
    {
        var source = new FakeSnapshotChangeSource();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        var refreshes = 0;
        var signalledDuringRepaint = false;

        using var pump = new WidgetSnapshotChangeWatcher(
            () =>
            {
                refreshes++;
                if (!signalledDuringRepaint)
                {
                    signalledDuringRepaint = true;
                    source.Raise();
                }
            },
            source,
            clock,
            Debounce,
            Backstop);

        pump.Arm();
        source.Raise();
        clock.Advance(Debounce);
        Assert.Equal(1, refreshes);

        clock.Advance(Debounce);
        Assert.Equal(2, refreshes);
    }

    /// <summary>
    /// Two triggers racing into the pump (a debounce window closing while a backstop re-read is running,
    /// or the reverse) must never paint concurrently, and the later trigger must not be dropped: the
    /// running pass loops once more instead.
    /// </summary>
    [Fact]
    public async Task Overlapping_triggers_never_repaint_concurrently_and_never_lose_the_second_one()
    {
        var source = new FakeSnapshotChangeSource();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var refreshes = 0;
        var depth = 0;
        var maxDepth = 0;

        using var pump = new WidgetSnapshotChangeWatcher(
            () =>
            {
                var current = Interlocked.Increment(ref depth);
                maxDepth = Math.Max(maxDepth, current);
                if (Interlocked.Increment(ref refreshes) == 1)
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                }

                Interlocked.Decrement(ref depth);
            },
            source,
            clock,
            Debounce,
            Backstop);

        pump.Arm();
        source.Raise();

        // The first repaint runs on a background thread and parks inside the callback.
        var firstPass = Task.Run(() => clock.Advance(Debounce));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "the first repaint never started");

        // A second trigger arrives while that pass is still running. It must be absorbed, not run in
        // parallel — and not dropped either.
        pump.TriggerForTesting();

        release.Set();
        await firstPass.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, maxDepth);
        Assert.Equal(2, refreshes);
    }
}
