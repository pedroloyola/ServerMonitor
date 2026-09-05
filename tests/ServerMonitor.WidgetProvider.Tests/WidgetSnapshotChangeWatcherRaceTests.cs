using Microsoft.Extensions.Time.Testing;
using ServerMonitor.WidgetProvider.Reading;
using ServerMonitor.WidgetProvider.Tests.Fakes;

namespace ServerMonitor.WidgetProvider.Tests;

/// <summary>
/// Regressions for the two concurrency defects found reviewing the repaint pump. Both are proved with
/// BARRIERS, not with elapsed time: each test pins the interleaving at the exact point the defect lives,
/// so it fails deterministically on the broken ordering and passes deterministically on the fixed one. No
/// test here asserts "nothing happened within N seconds" — that shape can pass by accident on a slow
/// machine and can miss an event that arrives late.
/// <list type="number">
/// <item><b>The late start.</b> The backstop read <c>IsWatching == false</c>, a complete
/// <c>Disarm</c> (including the source's own <c>Stop</c>) overtook it, and only then did it call
/// <c>Start</c> — leaving a live FileSystemWatcher behind a disarmed pump. The source serializes
/// Start/Stop internally, exactly as <see cref="FakeSnapshotChangeSource"/> does, so the gap that matters
/// is the one BEFORE the OS call, which is why the barrier sits on the <c>IsWatching</c> read.</item>
/// <item><b>The undrained disposal.</b> <c>Dispose</c> returned while the refresh callback was still
/// running, so "no callback runs after Shutdown" was not actually guaranteed under overlap.</item>
/// </list>
/// </summary>
public sealed class WidgetSnapshotChangeWatcherRaceTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(60);

    /// <summary>Generous: every barrier in this file is released by the test, never by elapsed time.</summary>
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(30);

    private static FakeTimeProvider NewClock() =>
        new(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

    private static WidgetSnapshotChangeWatcher NewPump(
        Action refresh, FakeSnapshotChangeSource source, FakeTimeProvider clock) =>
        new(refresh, source, clock, Debounce, Backstop);

    /// <summary>
    /// Waits for a state transition the code under test is about to make. This is a barrier, not a
    /// timing assumption: the condition is set by another thread that has already been released, and the
    /// timeout only turns a deadlock into a failure instead of a hang.
    /// </summary>
    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + BarrierTimeout;
        var spin = new SpinWait();
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            spin.SpinOnce();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 1. The late start: the source must never be left watching behind a disarmed pump.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// THE confirmed race. The backstop is held in the gap between "is a watch established?" and
    /// "establish one", a full <see cref="WidgetSnapshotChangeWatcher.Disarm"/> is allowed to complete in
    /// that gap, and only then is the backstop released. Before the fix the late <c>Start</c> won and the
    /// source ended up watching with the pump disarmed.
    /// </summary>
    [Fact]
    public async Task A_backstop_start_cannot_outlive_a_disarm_that_overtook_it()
    {
        var clock = NewClock();
        var source = new FakeSnapshotChangeSource { WatchEstablishes = false };
        var refreshes = 0;
        using var pump = NewPump(() => Interlocked.Increment(ref refreshes), source, clock);

        // Armed, but the watch could not be established (the snapshot directory does not exist yet), so
        // the backstop will try to establish one on its next tick.
        pump.Arm();
        Assert.False(source.IsWatching);
        source.WatchEstablishes = true;

        using var held = new ManualResetEventSlim(false);
        source.ParkFirstIsWatchingRead(held);

        var backstop = Task.Run(() => clock.Advance(Backstop));
        Assert.True(source.IsWatchingEntered.Wait(BarrierTimeout), "the backstop never reached the watch check");

        // A disarm overtakes the parked backstop. It is observably complete as far as pump state goes
        // (IsArmed is false) before the backstop is allowed to continue.
        var disarm = Task.Run(pump.Disarm);
        WaitUntil(() => !pump.IsArmed, "the disarm to take effect");

        held.Set();
        await Task.WhenAll(backstop, disarm).WaitAsync(BarrierTimeout);

        Assert.False(pump.IsArmed);
        Assert.False(source.IsWatching);
        Assert.Equal(0, Volatile.Read(ref refreshes));
    }

    /// <summary>
    /// The same ordering guarantee on the arm path: a <c>Start</c> that is still running when a disarm
    /// arrives is undone rather than left in place.
    /// </summary>
    [Fact]
    public async Task A_disarm_during_an_in_flight_start_never_leaves_the_source_watching()
    {
        var clock = NewClock();
        var source = new FakeSnapshotChangeSource();
        using var startHeld = new ManualResetEventSlim(false);
        source.BlockStart = startHeld;
        using var pump = NewPump(() => { }, source, clock);

        var arm = Task.Run(pump.Arm);
        Assert.True(source.StartEntered.Wait(BarrierTimeout), "Arm never reached the source");

        var disarm = Task.Run(pump.Disarm);
        WaitUntil(() => !pump.IsArmed, "the disarm to take effect");

        startHeld.Set();
        await Task.WhenAll(arm, disarm).WaitAsync(BarrierTimeout);

        Assert.False(pump.IsArmed);
        Assert.False(source.IsWatching);
        Assert.True(source.StopCount >= 1, "the source was never stopped");
    }

    /// <summary>
    /// A callback carrying a superseded generation must not undo the decision that replaced it: after a
    /// disarm and a re-arm pile up behind a parked backstop, the pump ends armed AND watching.
    /// </summary>
    [Fact]
    public async Task A_stale_generation_callback_cannot_override_the_newest_decision()
    {
        var clock = NewClock();
        var source = new FakeSnapshotChangeSource { WatchEstablishes = false };
        using var pump = NewPump(() => { }, source, clock);

        pump.Arm();
        source.WatchEstablishes = true;

        using var held = new ManualResetEventSlim(false);
        source.ParkFirstIsWatchingRead(held);

        var backstop = Task.Run(() => clock.Advance(Backstop));
        Assert.True(source.IsWatchingEntered.Wait(BarrierTimeout), "the backstop never reached the watch check");

        // Two further decisions are taken while the old-generation backstop is parked.
        var cycle = Task.Run(() =>
        {
            pump.Disarm();
            pump.Arm();
        });
        WaitUntil(() => !pump.IsArmed, "the disarm to take effect");

        held.Set();
        await Task.WhenAll(backstop, cycle).WaitAsync(BarrierTimeout);

        Assert.True(pump.IsArmed);
        Assert.True(source.IsWatching, "the newest decision (re-arm) did not survive the stale callback");
    }

    /// <summary>
    /// The invariant behind all of the above, under unsynchronized churn: once everything settles, the
    /// source is watching if and only if the pump is armed. Any ordering bug shows up as a mismatch.
    /// </summary>
    [Fact]
    public async Task Concurrent_arming_and_disarming_always_settle_with_the_source_agreeing_with_the_pump()
    {
        var clock = NewClock();
        var source = new FakeSnapshotChangeSource();
        using var pump = NewPump(() => { }, source, clock);

        var armers = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                pump.Arm();
            }
        });
        var disarmers = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                pump.Disarm();
            }
        });

        await Task.WhenAll(armers, disarmers).WaitAsync(BarrierTimeout);

        // Settle on a known final decision, then compare the two states.
        pump.Arm();
        Assert.True(pump.IsArmed);
        Assert.True(source.IsWatching);

        pump.Disarm();
        Assert.False(pump.IsArmed);
        Assert.False(source.IsWatching);
    }

    /// <summary>
    /// A watch that faulted (internal-buffer overflow) reports <c>IsWatching == false</c>; the backstop
    /// must re-establish it, not merely re-read the file.
    /// </summary>
    [Fact]
    public void The_backstop_reestablishes_a_watch_that_faulted_mid_life()
    {
        var clock = NewClock();
        var source = new FakeSnapshotChangeSource();
        var refreshes = 0;
        using var pump = NewPump(() => Interlocked.Increment(ref refreshes), source, clock);

        pump.Arm();
        Assert.True(source.IsWatching);

        // The overflow: events were lost, the watch is dead, and the source signals one unconditional
        // re-read on its way out.
        source.Fault();
        clock.Advance(Debounce);
        Assert.Equal(1, refreshes); // the fault's own signal still repainted
        Assert.False(source.IsWatching);

        clock.Advance(Backstop);

        Assert.True(source.IsWatching, "the backstop did not re-establish the faulted watch");
        Assert.Equal(2, source.StartCount);
        Assert.Equal(2, refreshes);
    }

    // ---------------------------------------------------------------------------------------------
    // 2. Disposal drains: no callback survives Dispose.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Dispose is entered while a refresh is running and the refresh is released only once Dispose is
    /// already inside its drain. If Dispose returned without draining, the callback would still be
    /// running at the assertion — the flag it sets on its way out would be false.
    /// </summary>
    [Fact]
    public async Task Dispose_does_not_return_while_a_refresh_callback_is_still_running()
    {
        var clock = NewClock();
        var source = new FakeSnapshotChangeSource();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var refreshFinished = false;

        var pump = NewPump(
            () =>
            {
                entered.Set();
                release.Wait(BarrierTimeout);
                Volatile.Write(ref refreshFinished, true);
            },
            source,
            clock);

        pump.Arm();
        var refresh = Task.Run(pump.TriggerForTesting);
        Assert.True(entered.Wait(BarrierTimeout), "the refresh never started");

        // Released from inside the drain: Dispose can only return by having waited for it.
        pump.DrainWaitEnteredForTesting = () => release.Set();
        pump.Dispose();

        Assert.True(
            Volatile.Read(ref refreshFinished),
            "Dispose returned while the refresh callback was still running");

        await refresh.WaitAsync(BarrierTimeout);
    }

    /// <summary>
    /// The drain's bounded-shutdown residual, proved without a real clock: a refresh that never returns
    /// must not hang the provider's exit. The drain timeout is reached on the injected clock and Dispose
    /// returns with that single pass still outstanding (documented and accepted, §30).
    /// </summary>
    [Fact]
    public async Task Dispose_is_bounded_when_a_refresh_never_returns()
    {
        var clock = NewClock();
        var source = new FakeSnapshotChangeSource();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var refreshFinished = false;

        var pump = NewPump(
            () =>
            {
                entered.Set();
                release.Wait(BarrierTimeout);
                Volatile.Write(ref refreshFinished, true);
            },
            source,
            clock);

        pump.Arm();
        var refresh = Task.Run(pump.TriggerForTesting);
        Assert.True(entered.Wait(BarrierTimeout), "the refresh never started");

        // Push the injected clock past the drain budget from inside the wait itself.
        pump.DrainWaitEnteredForTesting =
            () => clock.Advance(WidgetSnapshotChangeWatcher.DefaultDrainTimeout + TimeSpan.FromSeconds(1));
        pump.Dispose();

        Assert.False(Volatile.Read(ref refreshFinished), "the refresh was not still outstanding");

        release.Set();
        await refresh.WaitAsync(BarrierTimeout);
    }

    /// <summary>
    /// Nothing can start after Dispose returns either: the late arrivals are refused, not merely
    /// unscheduled. Driven through the source and both timers.
    /// </summary>
    [Fact]
    public async Task No_callback_runs_after_dispose_returns()
    {
        var clock = NewClock();
        var source = new FakeSnapshotChangeSource();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var refreshes = 0;

        var pump = NewPump(
            () =>
            {
                Interlocked.Increment(ref refreshes);
                entered.Set();
                release.Wait(BarrierTimeout);
            },
            source,
            clock);

        pump.Arm();
        var refresh = Task.Run(pump.TriggerForTesting);
        Assert.True(entered.Wait(BarrierTimeout), "the refresh never started");

        pump.DrainWaitEnteredForTesting = () => release.Set();
        pump.Dispose();
        await refresh.WaitAsync(BarrierTimeout);

        var after = Volatile.Read(ref refreshes);
        source.Raise();               // the source is unhooked, but even a stray signal must be inert
        pump.TriggerForTesting();     // the timers' own entry point
        clock.Advance(Backstop * 3);  // and any callback the clock could still deliver

        Assert.Equal(after, Volatile.Read(ref refreshes));
        Assert.False(source.HasSubscribers);
        Assert.Equal(1, source.DisposeCount);
    }

    /// <summary>
    /// A repaint that decides to shut the provider down disposes the pump from inside the pump's own
    /// callback. The drain must not wait for the calling thread — that would be a self-deadlock.
    /// </summary>
    [Fact]
    public async Task Dispose_called_from_inside_a_refresh_does_not_deadlock()
    {
        var clock = NewClock();
        var source = new FakeSnapshotChangeSource();
        WidgetSnapshotChangeWatcher? pump = null;
        var disposedFromCallback = false;

        pump = NewPump(
            () =>
            {
                if (Volatile.Read(ref disposedFromCallback))
                {
                    return;
                }

                Volatile.Write(ref disposedFromCallback, true);
                pump!.Dispose();
            },
            source,
            clock);

        var created = pump;
        created.Arm();

        await Task.Run(created.TriggerForTesting).WaitAsync(BarrierTimeout);

        Assert.True(Volatile.Read(ref disposedFromCallback));
        Assert.False(created.IsArmed);
        Assert.Equal(1, source.DisposeCount);
    }

    /// <summary>
    /// Disposal is ordered against an in-flight <c>Start</c> too: the source is disposed after that call
    /// completes, never underneath it, and the pump ends up not watching.
    /// </summary>
    [Fact]
    public async Task Dispose_during_an_in_flight_start_leaves_nothing_watching()
    {
        var clock = NewClock();
        var source = new FakeSnapshotChangeSource();
        using var startHeld = new ManualResetEventSlim(false);
        source.BlockStart = startHeld;
        var pump = NewPump(() => { }, source, clock);

        var arm = Task.Run(pump.Arm);
        Assert.True(source.StartEntered.Wait(BarrierTimeout), "Arm never reached the source");

        var dispose = Task.Run(pump.Dispose);
        WaitUntil(() => !pump.IsArmed, "disposal to take effect");

        startHeld.Set();
        await Task.WhenAll(arm, dispose).WaitAsync(BarrierTimeout);

        Assert.False(source.IsWatching);
        Assert.Equal(1, source.DisposeCount);
        Assert.False(source.HasSubscribers);
    }
}
