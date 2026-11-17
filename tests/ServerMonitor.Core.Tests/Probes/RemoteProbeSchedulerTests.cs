using System.Collections.Concurrent;
using System.Diagnostics;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Probes;

namespace ServerMonitor.Core.Tests.Probes;

public sealed class RemoteProbeSchedulerTests
{
    private static readonly Guid ServerA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ServerB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly RemoteProbeExecution Ok = new(true, 0, "out", "", false);

    [Fact]
    public void NoProbes_NeverReserves()
    {
        var scheduler = new RemoteProbeScheduler([], new HistoryGeneration());

        Assert.Equal(0, scheduler.Count);
        Assert.Null(scheduler.TryReserve(ServerA, CancellationToken.None));
        Assert.False(scheduler.IsInFlight(ServerA));
    }

    [Fact]
    public void InvalidProbes_AreLeftOut_WithReasonsThatNeverQuoteTheCommand()
    {
        var probes = new IRemoteProbe[]
        {
            new FakeProbe("ok"),
            new FakeProbe("Upper"),
            new FakeProbe("a..b"),
            new FakeProbe(".lead"),
            new FakeProbe(new string('a', RemoteProbeLimits.MaxIdLength + 1)),
            new FakeProbe("os") { OperatingSystem = ServerOperatingSystem.Unknown },
            new FakeProbe("multi") { Command = "uptime\nsecret-two" },
            new FakeProbe("long") { Command = new string('x', RemoteProbeLimits.MaxCommandLength + 1) },
            new FakeProbe("empty") { Command = "  " },
            new FakeProbe("cap0") { OutputLimitBytes = 0 },
            new FakeProbe("capbig") { OutputLimitBytes = RemoteProbeLimits.MaxOutputLimitBytes + 1 },
            new FakeProbe("t0") { Timeout = TimeSpan.Zero },
            new FakeProbe("tbig") { Timeout = RemoteProbeLimits.MaxTimeout + TimeSpan.FromSeconds(1) },
            new FakeProbe("neg") { MinimumInterval = TimeSpan.FromSeconds(-1) },
            new FakeProbe("ok"),
            new ThrowingPropertiesProbe(),
        };

        var scheduler = new RemoteProbeScheduler(probes!, new HistoryGeneration());

        Assert.Equal(1, scheduler.Count);
        Assert.Equal(probes.Length - 1, scheduler.Rejected.Count);
        Assert.DoesNotContain(scheduler.Rejected, reason => reason.Contains("secret-two", StringComparison.Ordinal));
        Assert.Contains(scheduler.Rejected, reason => reason.Contains("already has its id", StringComparison.Ordinal));
    }

    [Fact]
    public void Properties_AreReadOnce_AndLaterChangesDoNotRun()
    {
        var probe = new FakeProbe("once");
        var scheduler = new RemoteProbeScheduler([probe], new HistoryGeneration());
        probe.Command = "something-else";

        var batch = scheduler.TryReserve(ServerA, CancellationToken.None)!.Begin(ServerOperatingSystem.Linux)!;

        Assert.Equal("uptime", Assert.Single(batch.Requests).Command);
    }

    [Fact]
    public void OnlyProbesForTheServersOperatingSystem_AreDue()
    {
        var scheduler = new RemoteProbeScheduler(
            [new FakeProbe("linux"), new FakeProbe("mac") { OperatingSystem = ServerOperatingSystem.MacOS }],
            new HistoryGeneration());

        var batch = scheduler.TryReserve(ServerA, CancellationToken.None)!.Begin(ServerOperatingSystem.MacOS)!;

        Assert.Equal("mac", Assert.Single(batch.Requests).ProbeId);
    }

    [Fact]
    public void MinimumInterval_DecidesWhenAProbeIsDueAgain_PerServer()
    {
        var clock = new ManualClock();
        var scheduler = new RemoteProbeScheduler(
            [new FakeProbe("slow") { MinimumInterval = TimeSpan.FromMinutes(5) }],
            new HistoryGeneration(),
            clock);

        Assert.NotNull(BeginAndComplete(scheduler, ServerA));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Null(BeginAndComplete(scheduler, ServerA));
        Assert.NotNull(BeginAndComplete(scheduler, ServerB));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(BeginAndComplete(scheduler, ServerA));
    }

    [Fact]
    public void Forget_ClearsWhenProbesLastRan()
    {
        var clock = new ManualClock();
        var scheduler = new RemoteProbeScheduler(
            [new FakeProbe("slow") { MinimumInterval = TimeSpan.FromHours(1) }],
            new HistoryGeneration(),
            clock);

        Assert.NotNull(BeginAndComplete(scheduler, ServerA));
        scheduler.Forget(ServerA);

        Assert.NotNull(BeginAndComplete(scheduler, ServerA));
    }

    [Fact]
    public void AtMostMaxProbesPerPoll_LeastRecentlyRunFirst()
    {
        var clock = new ManualClock();
        var probes = Enumerable.Range(0, RemoteProbeLimits.MaxProbesPerPoll + 2)
            .Select(i => new FakeProbe($"p{i}"))
            .ToArray();
        var scheduler = new RemoteProbeScheduler(probes, new HistoryGeneration(), clock);

        var first = BeginAndComplete(scheduler, ServerA)!;
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = BeginAndComplete(scheduler, ServerA)!;

        Assert.Equal(["p0", "p1", "p2", "p3"], first.Requests.Select(request => request.ProbeId));
        Assert.Equal(["p4", "p5", "p0", "p1"], second.Requests.Select(request => request.ProbeId));
    }

    [Fact]
    public void TotalOutputBudget_LeavesOutProbesThatDoNotFit()
    {
        var big = RemoteProbeLimits.MaxOutputLimitBytes;
        var scheduler = new RemoteProbeScheduler(
            [
                new FakeProbe("a") { OutputLimitBytes = big },
                new FakeProbe("b") { OutputLimitBytes = big },
                new FakeProbe("c") { OutputLimitBytes = big },
                new FakeProbe("d") { OutputLimitBytes = big - 1 },
                new FakeProbe("e") { OutputLimitBytes = 2 },
            ],
            new HistoryGeneration());

        var batch = BeginAndComplete(scheduler, ServerA)!;

        Assert.Equal(["a", "b", "c", "d"], batch.Requests.Select(request => request.ProbeId));
        Assert.True(batch.Requests.Sum(request => request.OutputLimitBytes) <= RemoteProbeLimits.TotalOutputBudgetBytes);
    }

    [Fact]
    public void WhilePhaseTwoIsInFlight_TheNextPollGetsNoReservation()
    {
        var scheduler = new RemoteProbeScheduler([new FakeProbe("p")], new HistoryGeneration());

        var first = scheduler.TryReserve(ServerA, CancellationToken.None)!;

        Assert.Null(scheduler.TryReserve(ServerA, CancellationToken.None));
        Assert.NotNull(scheduler.TryReserve(ServerB, CancellationToken.None));
        first.Complete();
        first.Complete();
        Assert.NotNull(scheduler.TryReserve(ServerA, CancellationToken.None));
    }

    [Fact]
    public void Begin_RunsOnce_AndNotAfterComplete()
    {
        var scheduler = new RemoteProbeScheduler([new FakeProbe("p")], new HistoryGeneration());
        var reservation = scheduler.TryReserve(ServerA, CancellationToken.None)!;

        Assert.NotNull(reservation.Begin(ServerOperatingSystem.Linux));
        Assert.Null(reservation.Begin(ServerOperatingSystem.Linux));

        var other = scheduler.TryReserve(ServerB, CancellationToken.None)!;
        other.Complete();
        Assert.Null(other.Begin(ServerOperatingSystem.Linux));
    }

    [Fact]
    public void CancelledServerToken_GivesNoReservation()
    {
        var scheduler = new RemoteProbeScheduler([new FakeProbe("p")], new HistoryGeneration());

        Assert.Null(scheduler.TryReserve(ServerA, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Run_DeliversEachOutcome_WithGenerationAndServer()
    {
        var generation = new HistoryGeneration();
        generation.Advance();
        var probe = new FakeProbe("p");
        var scheduler = new RemoteProbeScheduler([probe], generation);
        var batch = scheduler.TryReserve(ServerA, CancellationToken.None)!.Begin(ServerOperatingSystem.Linux)!;

        await batch.RunAsync((request, _) => Task.FromResult(Ok));

        var outcome = Assert.Single(probe.Outcomes);
        Assert.Equal(ServerA, outcome.ServerId);
        Assert.Equal("p", outcome.ProbeId);
        Assert.Equal(1, outcome.HistoryGeneration);
        Assert.Equal(RemoteProbeOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("out", outcome.StandardOutput);
        Assert.DoesNotContain("out", outcome.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonZeroExit_IsDeliveredAsCompleted_WithItsStatusAndOutput()
    {
        var probe = new FakeProbe("p");
        var batch = Reserve(probe);

        await batch.RunAsync((_, _) => Task.FromResult(new RemoteProbeExecution(true, 3, "partial", "err", false)));

        var outcome = Assert.Single(probe.Outcomes);
        Assert.Equal(RemoteProbeOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(3, outcome.ExitStatus);
        Assert.Equal("partial", outcome.StandardOutput);
        Assert.Equal("err", outcome.StandardError);
    }

    [Fact]
    public async Task OutputOverTheLimit_IsReportedWithoutOutput_EvenWhenTheExecutorDidNotFlagIt()
    {
        var flagged = new FakeProbe("flagged");
        var unflagged = new FakeProbe("unflagged") { OutputLimitBytes = 4 };
        var batch = Reserve(flagged, unflagged);

        await batch.RunAsync((request, _) => Task.FromResult(request.ProbeId == "flagged"
            ? new RemoteProbeExecution(true, 0, null, null, true)
            : new RemoteProbeExecution(true, 0, "ééé", "", false)));

        Assert.Equal(RemoteProbeOutcomeKind.OutputExceededLimit, Assert.Single(flagged.Outcomes).Kind);
        var outcome = Assert.Single(unflagged.Outcomes);
        Assert.Equal(RemoteProbeOutcomeKind.OutputExceededLimit, outcome.Kind);
        Assert.Null(outcome.StandardOutput);
    }

    [Fact]
    public async Task NotExecuted_UndecodableOrThrowing_IsFailed_AndTheNextProbeStillRuns()
    {
        var notRun = new FakeProbe("a");
        var undecodable = new FakeProbe("b");
        var throwing = new FakeProbe("c");
        var last = new FakeProbe("d");
        var batch = Reserve(notRun, undecodable, throwing, last);

        await batch.RunAsync((request, _) => request.ProbeId switch
        {
            "a" => Task.FromResult(new RemoteProbeExecution(false, null, null, null, false)),
            "b" => Task.FromResult(new RemoteProbeExecution(true, 0, null, null, false)),
            "c" => throw new InvalidOperationException("boom"),
            _ => Task.FromResult(Ok),
        });

        Assert.Equal(RemoteProbeOutcomeKind.Failed, Assert.Single(notRun.Outcomes).Kind);
        Assert.Equal(RemoteProbeOutcomeKind.Failed, Assert.Single(undecodable.Outcomes).Kind);
        Assert.Equal(RemoteProbeOutcomeKind.Failed, Assert.Single(throwing.Outcomes).Kind);
        Assert.Equal(RemoteProbeOutcomeKind.Completed, Assert.Single(last.Outcomes).Kind);
    }

    [Fact]
    public async Task ThrowingSink_DoesNotStopTheOtherProbes()
    {
        var throwing = new FakeProbe("a") { ThrowOnAccept = true };
        var next = new FakeProbe("b");
        var batch = Reserve(throwing, next);

        await batch.RunAsync((_, _) => Task.FromResult(Ok));

        Assert.Single(throwing.Outcomes);
        Assert.Single(next.Outcomes);
    }

    [Fact]
    public async Task Timeout_IsReported_EvenWhenTheExecutorIgnoresItsToken()
    {
        var stuck = new FakeProbe("stuck") { Timeout = TimeSpan.FromMilliseconds(100) };
        var next = new FakeProbe("next");
        var batch = Reserve(stuck, next);
        var never = new TaskCompletionSource<RemoteProbeExecution>();

        var watch = Stopwatch.StartNew();
        await batch.RunAsync((request, _) => request.ProbeId == "stuck" ? never.Task : Task.FromResult(Ok));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(RemoteProbeOutcomeKind.TimedOut, Assert.Single(stuck.Outcomes).Kind);
        Assert.Null(stuck.Outcomes[0].StandardOutput);
        Assert.Single(next.Outcomes);
    }

    [Fact]
    public async Task Timeout_CancelsTheTokenGivenToTheExecutor()
    {
        var probe = new FakeProbe("p") { Timeout = TimeSpan.FromMilliseconds(100) };
        var batch = Reserve(probe);
        var sawCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await batch.RunAsync(async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                sawCancellation.TrySetResult();
                throw;
            }

            return Ok;
        });

        // The batch stops waiting on the same token, so the executor may notice it a moment later.
        await sawCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RemoteProbeOutcomeKind.TimedOut, Assert.Single(probe.Outcomes).Kind);
    }

    [Fact]
    public async Task Forget_CancelsPhaseTwo_AndNothingMoreIsDelivered()
    {
        var first = new FakeProbe("a");
        var second = new FakeProbe("b");
        var scheduler = new RemoteProbeScheduler([first, second], new HistoryGeneration());
        var reservation = scheduler.TryReserve(ServerA, CancellationToken.None)!;
        var batch = reservation.Begin(ServerOperatingSystem.Linux)!;
        var started = new TaskCompletionSource();

        var run = batch.RunAsync(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Ok;
        });
        await started.Task;
        scheduler.Forget(ServerA);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        reservation.Complete();

        Assert.True(batch.CancellationToken.IsCancellationRequested);
        Assert.Empty(first.Outcomes);
        Assert.Empty(second.Outcomes);
        Assert.False(scheduler.IsInFlight(ServerA));
    }

    [Fact]
    public async Task ServerTokenCancellation_CancelsPhaseTwo()
    {
        using var server = new CancellationTokenSource();
        var probe = new FakeProbe("a");
        var scheduler = new RemoteProbeScheduler([probe], new HistoryGeneration());
        var batch = scheduler.TryReserve(ServerA, server.Token)!.Begin(ServerOperatingSystem.Linux)!;
        var started = new TaskCompletionSource();

        var run = batch.RunAsync(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Ok;
        });
        await started.Task;
        server.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(probe.Outcomes);
    }

    [Fact]
    public async Task HistoryClear_CancelsPhaseTwo_AndDropsAStaleOutcome()
    {
        var generation = new HistoryGeneration();
        var probe = new FakeProbe("a");
        var scheduler = new RemoteProbeScheduler([probe], generation);
        var batch = scheduler.TryReserve(ServerA, CancellationToken.None)!.Begin(ServerOperatingSystem.Linux)!;

        // The executor ignores its token and finishes after the clear: its outcome is stale and is dropped.
        await batch.RunAsync((_, _) =>
        {
            generation.Advance();
            return Task.FromResult(Ok);
        });

        Assert.True(batch.CancellationToken.IsCancellationRequested);
        Assert.Empty(probe.Outcomes);
    }

    [Fact]
    public void GenerationMovedBeforeBegin_PicksNothing()
    {
        var generation = new HistoryGeneration();
        var probe = new FakeProbe("a");
        var scheduler = new RemoteProbeScheduler([probe], generation);
        var reservation = scheduler.TryReserve(ServerA, CancellationToken.None)!;
        generation.Advance();

        Assert.Null(reservation.Begin(ServerOperatingSystem.Linux));
        Assert.Empty(probe.Outcomes);
    }

    [Fact]
    public async Task ConcurrentReservations_ForOneServer_OnlyOneWins()
    {
        var scheduler = new RemoteProbeScheduler([new FakeProbe("p")], new HistoryGeneration());
        var winners = new ConcurrentBag<RemoteProbeReservation>();

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            if (scheduler.TryReserve(ServerA, CancellationToken.None) is { } reservation)
            {
                winners.Add(reservation);
            }
        })));

        Assert.Single(winners);
    }

    private static RemoteProbeBatch Reserve(params IRemoteProbe[] probes) =>
        new RemoteProbeScheduler(probes, new HistoryGeneration())
            .TryReserve(ServerA, CancellationToken.None)!
            .Begin(ServerOperatingSystem.Linux)!;

    private static RemoteProbeBatch? BeginAndComplete(RemoteProbeScheduler scheduler, Guid serverId)
    {
        var reservation = scheduler.TryReserve(serverId, CancellationToken.None)!;
        try
        {
            return reservation.Begin(ServerOperatingSystem.Linux);
        }
        finally
        {
            reservation.Complete();
        }
    }

    private sealed class FakeProbe(string id) : IRemoteProbe
    {
        public string Id { get; set; } = id;

        public ServerOperatingSystem OperatingSystem { get; set; } = ServerOperatingSystem.Linux;

        public string Command { get; set; } = "uptime";

        public int OutputLimitBytes { get; set; } = 1024;

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

        public TimeSpan MinimumInterval { get; set; } = TimeSpan.Zero;

        public bool ThrowOnAccept { get; init; }

        public List<RemoteProbeOutcome> Outcomes { get; } = [];

        public void Accept(RemoteProbeOutcome outcome)
        {
            Outcomes.Add(outcome);
            if (ThrowOnAccept)
            {
                throw new InvalidOperationException("sink failed");
            }
        }
    }

    private sealed class ThrowingPropertiesProbe : IRemoteProbe
    {
        public string Id => throw new InvalidOperationException();

        public ServerOperatingSystem OperatingSystem => ServerOperatingSystem.Linux;

        public string Command => "uptime";

        public int OutputLimitBytes => 1;

        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public TimeSpan MinimumInterval => TimeSpan.Zero;

        public void Accept(RemoteProbeOutcome outcome)
        {
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
