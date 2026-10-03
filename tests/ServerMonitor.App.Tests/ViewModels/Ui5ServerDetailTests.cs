using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Domain;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Interfaces;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.5 §3: the Server Detail view model over the REAL dashboard and its live cards (Ui4TestKit). Synthetic data only;
/// no wall clock (FakeTimeProvider), no dispatcher (the flush runs inline unless a test installs the scheduler seam).
/// </summary>
public sealed class Ui5ServerDetailTests
{
    private static readonly DateTimeOffset Now = Ui4TestKit.Now;

    // ---- header, shared status, H-UI5-2 ---------------------------------------------------------------------------

    [Theory]
    [InlineData(ServerHealth.Healthy)]
    [InlineData(ServerHealth.Warning)]
    [InlineData(ServerHealth.Critical)]
    [InlineData(ServerHealth.Offline)]
    [InlineData(ServerHealth.Unknown)]
    public async Task TheStatusText_IsTheRowsText_ForEveryHealth(ServerHealth health)
    {
        var kit = await LoadedAsync(new Ui4TestKit.Fleet().Add("web", health, 10, 20, 30), new ResWLocalizationService("pt-PT"));
        using var detail = Open(kit, "web");
        using var row = new ServerDirectoryRowViewModel(detail.Card!, kit.Localization, _ => { });

        Assert.Equal(row.StatusDisplay, detail.StatusText);
        Assert.Equal(kit.Localization.GetString(ServerStatusPresentation.StatusKey(health)), detail.StatusText);
        Assert.DoesNotContain("Offline", detail.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OfflineWithARetainedReading_ShowsTheReading_MarkedStale_WhileTheRowShowsADash()
    {
        var fleet = new Ui4TestKit.Fleet().Add("nas", ServerHealth.Offline, 12, 33, 61, lastSuccess: Now.AddMinutes(-42));
        var kit = await LoadedAsync(fleet, new ResWLocalizationService("pt-PT"));
        kit.States.Set(kit.States.Get(fleet.IdOf("nas")) with { IsStale = true, LastAttemptAt = Now.AddSeconds(-8) });
        using var detail = Open(kit, "nas");
        using var row = new ServerDirectoryRowViewModel(detail.Card!, kit.Localization, _ => { });

        Assert.Equal("12%", detail.CpuDisplay);
        Assert.Equal("—", row.CpuDisplay);
        Assert.True(detail.IsReadingStale);
        Assert.True(detail.IsWithoutConnection);
        // Boss B2 decision: the stale text and "Última atualização" share one timestamp and one clock read (42, not 41).
        Assert.Equal("Última atualização há 42 min", detail.StaleText);
        Assert.Equal("Há 42 minutos", detail.LastUpdatedValue);
        Assert.Contains(detail.StaleText!, detail.AutomationSummary, StringComparison.Ordinal); // legible without colour
    }

    /// <summary>
    /// Boss B2 decision (single source): at a minute boundary the stale text and the "Última atualização" value cannot
    /// disagree - both come from the engine's last success and ONE clock read per reading. The engine's attempt timestamp
    /// (which StaleAgeDisplay used, 42 min) is deliberately not a second source; a later clock tick changes nothing until
    /// the next reading.
    /// </summary>
    [Fact]
    public async Task StaleText_AndLastUpdated_ShareOneTimestamp_AndOneClockRead()
    {
        var success = Now.AddMinutes(-42).AddMilliseconds(1); // 41 min 59.999 s before the clock
        var fleet = new Ui4TestKit.Fleet().Add("nas", ServerHealth.Offline, 12, 33, 61, lastSuccess: success);
        var kit = await LoadedAsync(fleet, new ResWLocalizationService("pt-PT"));
        kit.States.Set(kit.States.Get(fleet.IdOf("nas")) with { IsStale = true, LastAttemptAt = success.AddMinutes(42) });
        var clock = new FakeTimeProvider(Now);
        using var detail = Open(kit, "nas", clock: new PresentationClock(clock));

        Assert.Equal("Última atualização há 41 min", detail.StaleText);
        Assert.Equal("Há 41 minutos", detail.LastUpdatedValue);

        clock.Advance(TimeSpan.FromMilliseconds(5)); // crosses the minute boundary, but no new reading arrived
        Assert.Equal("Última atualização há 41 min", detail.StaleText);
        Assert.Equal("Há 41 minutos", detail.LastUpdatedValue);

        kit.States.Set(kit.States.Get(fleet.IdOf("nas")) with { ConsecutiveFailures = 5 }); // the next reading: one new read
        Assert.Equal("Última atualização há 42 min", detail.StaleText);
        Assert.Equal("Há 42 minutos", detail.LastUpdatedValue);
    }

    [Fact]
    public async Task OfflineWithASnapshot_ButNoEngineTiming_StillSaysStale_InWords()
    {
        var fleet = new Ui4TestKit.Fleet().Add("nas", ServerHealth.Offline, 12, 33, 61);
        var kit = await LoadedAsync(fleet, new ResWLocalizationService("pt-PT"));
        using var detail = Open(kit, "nas");

        Assert.True(detail.IsReadingStale);
        Assert.Equal("Leitura desatualizada", detail.StaleText); // under a minute: never a rounded-up "há 1 min"
        Assert.Equal("Há 8 segundos", detail.LastUpdatedValue);
    }

    [Fact]
    public async Task TheHeader_IsTheServer_WithTheA13Address_AndAnH1Title()
    {
        var fleet = new Ui4TestKit.Fleet()
            .Add("v6", ServerHealth.Healthy, 10, 20, 30, host: "2001:db8::10")
            .Add("web", ServerHealth.Healthy, 10, 20, 30, port: 2222);
        var kit = await LoadedAsync(fleet);

        using var v6 = Open(kit, "v6");
        using var web = Open(kit, "web");

        Assert.Equal("v6", v6.Title);
        Assert.Equal("[2001:db8::10]:22", v6.Address);
        Assert.Equal("web.local:2222", web.Address);
        Assert.Equal("OperatingSystemLinux", web.ConfiguredSystemDisplay);
    }

    [Fact]
    public async Task ActionNames_CarryTheServer_InEveryCulture()
    {
        foreach (var culture in ResWLocalizationService.Cultures)
        {
            var kit = await LoadedAsync(new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3), new ResWLocalizationService(culture));
            using var detail = Open(kit, "web");

            foreach (var name in new[]
                     {
                         detail.RefreshAutomationName, detail.EditAutomationName, detail.MoreActionsAutomationName,
                         detail.HideAutomationName, detail.RemoveAutomationName, detail.ViewHistoryAutomationName,
                         detail.ViewWorkloadsAutomationName
                     })
            {
                Assert.Contains("web", name, StringComparison.Ordinal);
            }

            Assert.False(string.IsNullOrWhiteSpace(detail.OperationErrorAutomationName));
        }
    }

    // ---- metrics: unknown ≠ zero, A-4, severity -------------------------------------------------------------------

    [Fact]
    public async Task AnUnknownMetric_IsADash_AndAnEmptyTrack_WhileZeroIsAValidReading()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, cpu: null, mem: 0, disk: null);
        var kit = await LoadedAsync(fleet, new ResWLocalizationService("pt-PT"));
        using var detail = Open(kit, "web");

        Assert.False(detail.HasCpuPercent);
        Assert.Equal("—", detail.CpuDisplay);
        Assert.Equal("sem dados", detail.CpuAccessibleValue);
        Assert.Null(detail.DiskLitSegments);
        Assert.Equal("0%", detail.MemoryDisplay);
        Assert.Equal(0, detail.MemoryLitSegments);
    }

    [Fact]
    public async Task Segments_AndSeverity_FollowA4_AndTheEngineThresholds()
    {
        var fleet = new Ui4TestKit.Fleet().Add("db", ServerHealth.Critical, cpu: 97, mem: 62, disk: 48);
        var kit = await LoadedAsync(fleet);
        using var detail = Open(kit, "db");

        Assert.Equal(17, detail.MemoryLitSegments); // Figma 112:1855: 62% → 17/28
        Assert.Equal(7, detail.DiskLitSegments);    // Figma 112:1890: 48% → 7/14
        Assert.Equal(ServerHealth.Critical, detail.CpuSeverity);
        Assert.Equal(ServerHealth.Healthy, detail.DiskSeverity);
    }

    // ---- derived states -------------------------------------------------------------------------------------------

    [Fact]
    public async Task DerivedStates_ComeFromTheCard_NeverFromANewClassification()
    {
        var fleet = new Ui4TestKit.Fleet()
            .Add("new", ServerHealth.Unknown, snapshot: false)
            .Add("broken", ServerHealth.Unknown, snapshot: false)
            .Add("busy", ServerHealth.Healthy, 10, 20, 30);
        var kit = await LoadedAsync(fleet);
        kit.States.Set(kit.States.Get(fleet.IdOf("broken")) with { LastError = MetricsCollectionErrorCode.ConnectionFailed, LastAttemptAt = Now });
        kit.States.Set(kit.States.Get(fleet.IdOf("busy")) with { IsRefreshing = true });

        using var first = Open(kit, "new");
        using var broken = Open(kit, "broken");
        using var busy = Open(kit, "busy");

        Assert.True(first.IsFirstReading);
        Assert.False(first.HasReading);
        Assert.Null(first.LastUpdatedValue);
        Assert.True(broken.HasCollectionError);
        Assert.Equal("Metrics could not be refreshed.", broken.CollectionErrorText);
        Assert.True(busy.IsRefreshing);
        Assert.False(busy.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnUnsupportedSystem_HasNoMetricsStory()
    {
        var fleet = new Ui4TestKit.Fleet().Add("win", ServerHealth.Unknown, snapshot: false);
        var kit = await LoadedAsync(fleet);
        var index = kit.Servers.Servers.FindIndex(server => server.Name == "win");
        kit.Servers.Servers[index] = kit.Servers.Servers[index] with { OperatingSystem = ServerOperatingSystem.Unknown };
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "win");

        Assert.True(detail.IsMetricsUnsupported);
        Assert.False(detail.IsFirstReading);
    }

    [Theory]
    [InlineData(ServerConnectionState.AuthenticationFailed, true)]
    [InlineData(ServerConnectionState.HostKeyUnknown, true)]
    [InlineData(ServerConnectionState.HostKeyMismatch, true)]
    [InlineData(ServerConnectionState.Connected, false)]
    [InlineData(ServerConnectionState.Unreachable, false)]
    public async Task ConnectionProblems_ComeFromTheConnectionStateStore_AndUpdateLive(ServerConnectionState state, bool problem)
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Unknown, snapshot: false);
        var kit = await LoadedAsync(fleet);
        using var detail = Open(kit, "web");
        var raised = Recorder(detail);

        kit.Connections.Set(fleet.IdOf("web"), new SshConnectionResult { State = state });

        Assert.Equal(state, detail.ConnectionState);
        Assert.Equal(problem, detail.HasConnectionProblem);
        Assert.Equal($"ConnectionState{state}", detail.ConnectionStateDisplay);
        Assert.Contains(nameof(ServerDetailViewModel.HasConnectionProblem), raised);
    }

    [Fact]
    public async Task ARoutedServer_SaysSo_WithoutExposingTheJumpHost()
    {
        var fleet = new Ui4TestKit.Fleet().Add("internal", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        var index = kit.Servers.Servers.FindIndex(server => server.Name == "internal");
        kit.Servers.Servers[index] = kit.Servers.Servers[index] with
        {
            Route = new ServerRoute { Jump = new JumpHop { Host = "bastion.local", Username = "qa" } }
        };
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "internal");

        Assert.True(detail.IsRouted);
        var strings = typeof(ServerDetailViewModel).GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
            .Select(p => (string?)p.GetValue(detail) ?? string.Empty);
        Assert.DoesNotContain(strings, value => value.Contains("bastion", StringComparison.Ordinal));
    }

    // ---- A-5 last update (D-UI3-9, no timer) ----------------------------------------------------------------------

    [Fact]
    public async Task LastUpdate_IsLabelPlusValue_FromTheEnginesLastSuccess_AndHasNoTimer()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet, new ResWLocalizationService("pt-PT"));
        var clock = new FakeTimeProvider(Now);
        using var detail = Open(kit, "web", clock: new PresentationClock(clock));
        var raised = Recorder(detail);

        Assert.Equal("Última atualização", detail.LastUpdatedLabel);
        Assert.Equal("Há 8 segundos", detail.LastUpdatedValue); // Figma 112:1912 long form

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.DoesNotContain(nameof(ServerDetailViewModel.LastUpdatedValue), raised); // nothing ticks

        kit.States.Set(kit.States.Get(fleet.IdOf("web")) with { LastSuccessAt = clock.GetUtcNow(), LastAttemptAt = clock.GetUtcNow() });
        Assert.Contains(nameof(ServerDetailViewModel.LastUpdatedValue), raised);
        Assert.Equal("Agora mesmo", detail.LastUpdatedValue);
    }

    // ---- pass-through commands, H-UI5-1 exit, return focus ---------------------------------------------------------

    [Fact]
    public async Task Refresh_History_AndWorkloads_PassStraightThroughTheLiveCard()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        var focus = new ServerDetailReturnFocus();
        using var detail = Open(kit, "web", returnFocus: focus);
        var id = fleet.IdOf("web");

        detail.RefreshCommand.Execute(null);
        detail.ViewHistoryCommand.Execute(null);
        Assert.Equal(1, kit.Engine.RefreshNowCount);
        Assert.Equal(id, kit.Engine.LastRefreshedServerId);
        Assert.Equal(id, kit.Navigation.LastHistoryServerId);

        using (var back = Open(kit, "web", returnFocus: focus))
        {
            Assert.Equal(ServerDetailReturnTarget.History, back.TakeReturnFocus());
            Assert.Equal(ServerDetailReturnTarget.None, back.TakeReturnFocus()); // once
        }

        detail.ViewWorkloadsCommand.Execute(null);
        Assert.Equal(id, kit.Navigation.LastWorkloadsServerId);
        using var again = Open(kit, "web", returnFocus: focus);
        Assert.Equal(ServerDetailReturnTarget.Workloads, again.TakeReturnFocus());
    }

    [Fact]
    public async Task TheReturnFocus_BelongsToItsServer_Only()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3).Add("db", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        var focus = new ServerDetailReturnFocus();
        using var web = Open(kit, "web", returnFocus: focus);
        web.ViewHistoryCommand.Execute(null);

        using var db = Open(kit, "db", returnFocus: focus);
        Assert.Equal(ServerDetailReturnTarget.None, db.TakeReturnFocus());
    }

    [Fact]
    public async Task Edit_OpensTheCurrentEditor_ForThatServer()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var dialogs = new RecordingDialogs();
        var kit = await LoadedAsync(fleet, dialogs: dialogs);
        using var detail = Open(kit, "web");

        detail.EditCommand.Execute(null);

        Assert.Equal(fleet.IdOf("web"), Assert.Single(dialogs.Edited).Id);
    }

    /// <summary>H-UI5-1: Ocultar started HERE returns to Servidores (where its toast lives), even from the Visão geral.</summary>
    [Fact]
    public async Task HidingFromTheDetail_ReturnsToServers_Once()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3).Add("db", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        kit.Servers.HideOverride = HideIn(kit);
        using var detail = Open(kit, "web", ServerDetailOrigin.Overview);

        await ((AsyncRelayCommand)detail.HideCommand).ExecuteAsync();

        Assert.Equal(1, kit.Navigation.ServersCount);
        Assert.Equal(0, kit.Navigation.DashboardCount);
    }

    [Fact]
    public async Task RemovingFromTheDetail_KeepsTheConfirmation_AndReturnsToServers()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var dialogs = new RecordingDialogs { ConfirmRemove = true };
        Ui4TestKit.Harness? kitRef = null;
        var profiles = new RemovingProfiles(id => kitRef!);
        var kit = await LoadedAsync(fleet, dialogs: dialogs, profiles: profiles);
        kitRef = kit;
        using var detail = Open(kit, "web", ServerDetailOrigin.Overview);

        await ((AsyncRelayCommand)detail.RemoveCommand).ExecuteAsync();

        Assert.Equal(fleet.IdOf("web"), Assert.Single(dialogs.ConfirmedRemovals).Id); // the existing confirmation
        Assert.Equal(1, kit.Navigation.ServersCount);
        Assert.Equal(0, kit.Navigation.DashboardCount);
    }

    [Fact]
    public async Task AServerHiddenElsewhere_StillReturnsToTheOrigin()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        using var detail = Open(kit, "web", ServerDetailOrigin.Overview);

        await HideIn(kit)(fleet.IdOf("web"));

        Assert.Equal(1, kit.Navigation.DashboardCount);
        Assert.Equal(0, kit.Navigation.ServersCount);
    }

    [Fact]
    public async Task AFailedHideHere_ThenALaterDisappearance_ReturnsToTheOrigin()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        kit.Servers.HideOverride = _ => Task.FromResult(false);
        using var detail = Open(kit, "web", ServerDetailOrigin.Overview);

        await ((AsyncRelayCommand)detail.HideCommand).ExecuteAsync();
        Assert.True(detail.IsOperationErrorOpen);

        await HideIn(kit)(fleet.IdOf("web"));
        Assert.Equal(1, kit.Navigation.DashboardCount);
        Assert.Equal(0, kit.Navigation.ServersCount);
    }

    // ---- update path: re-subscription, disposal, coalescing, allocation -------------------------------------------

    [Fact]
    public async Task AnEdit_SwapsTheCard_AndTheDetailFollowsOnlyTheNewOne()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        using var detail = Open(kit, "web");
        var old = detail.Card!;
        Assert.True(IsSubscribed(old, detail));

        await kit.Dashboard.LoadAsync(); // an edit rebuilds the list: new card instances
        var fresh = detail.Card!;

        Assert.NotSame(old, fresh);
        Assert.False(IsSubscribed(old, detail));
        Assert.True(IsSubscribed(fresh, detail));
        var raised = Recorder(detail);
        old.ApplyMonitoringState(kit.States.Get(fleet.IdOf("web")));
        Assert.Empty(raised);
        fresh.ApplyMonitoringState(kit.States.Get(fleet.IdOf("web")));
        Assert.Contains(nameof(ServerDetailViewModel.CpuDisplay), raised);
    }

    [Fact]
    public async Task ADisposedDetail_ReceivesNothing_AndLeavesNoHandlerBehind()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        var card = kit.Dashboard.VisibleServers.Single();
        var before = HandlerCount(card);
        var detail = Open(kit, "web");
        Assert.Equal(before + 1, HandlerCount(card));
        var raised = Recorder(detail);

        detail.Dispose();
        detail.Dispose();
        card.ApplyMonitoringState(kit.States.Get(fleet.IdOf("web")));
        kit.Dashboard.IsOperationErrorOpen = true;

        Assert.Equal(before, HandlerCount(card));
        Assert.Empty(raised);
    }

    [Fact]
    public async Task ManyCycles_NeverGrowTheHandlers()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        using var detail = Open(kit, "web");
        var card = detail.Card!;
        var count = HandlerCount(card);

        for (var i = 0; i < 50; i++)
        {
            card.ApplyMonitoringState(kit.States.Get(fleet.IdOf("web")));
        }

        Assert.Equal(count, HandlerCount(card));
    }

    /// <summary>Two engine bursts → exactly two flushes, each raising every affected property ONCE (no string.Empty fan-out).</summary>
    [Fact]
    public async Task EachBurst_IsCoalescedIntoOneFlush()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        using var detail = Open(kit, "web");
        var queued = new List<Action>();
        detail.Scheduler = action =>
        {
            queued.Add(action);
            return true;
        };
        var raised = Recorder(detail);
        var card = detail.Card!;
        var id = fleet.IdOf("web");

        card.ApplyMonitoringState(kit.States.Get(id));         // burst 1: ~31 card notifications
        card.ApplyMonitoringState(kit.States.Get(id));         // same burst, still queued
        Assert.Single(queued);
        Assert.Empty(raised);
        queued[0]();

        kit.Metrics.Snapshots[id] = kit.Metrics.Snapshots[id] with { CpuUsagePercent = 55 };
        card.ApplyMonitoringState(kit.States.Get(id));         // burst 2
        Assert.Equal(2, queued.Count);
        queued[1]();

        Assert.Equal(2, raised.Count(name => name == nameof(ServerDetailViewModel.AutomationSummary)));
        Assert.Equal(2, raised.Count(name => name == nameof(ServerDetailViewModel.CpuDisplay)));
        Assert.DoesNotContain(string.Empty, raised);
        Assert.DoesNotContain(null, raised);
        Assert.Equal("55%", detail.CpuDisplay);
    }

    /// <summary>
    /// SPEC §5 perf (measured, not guessed) - Cortex B1 N-6: robust against tiered JIT. Both Detail paths are warmed first
    /// (inline flush, and the production-like coalesced flush with an available history), each configuration is measured
    /// three times and the MINIMUM kept, and a small explicit tolerance absorbs runtime noise. The coalesced variant also
    /// proves the per-tick path makes no history query when the last success does not move.
    /// </summary>
    [Fact]
    public async Task ACycle_AllocatesNothingExtra_ForTheDetail_InlineAndCoalesced()
    {
        const int cycles = 200;
        const long tolerance = 256; // bytes per 200 cycles - far below one allocation per tick (200 × 24 B)
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        var id = fleet.IdOf("web");
        var card = kit.Dashboard.VisibleServers.Single();
        var state = kit.States.Get(id);
        Action? pending = null;
        Func<Action, bool> scheduler = action =>
        {
            pending = action;
            return true;
        };

        void Cycle()
        {
            card.ApplyMonitoringState(state);
            var flush = pending;
            pending = null;
            flush?.Invoke();
        }

        long Min3()
        {
            var best = long.MaxValue;
            for (var round = 0; round < 3; round++)
            {
                for (var i = 0; i < 20; i++)
                {
                    Cycle(); // warm-up of this exact configuration
                }

                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < cycles; i++)
                {
                    Cycle();
                }

                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return best;
        }

        // Warm both Detail code paths once, then release them.
        using (var warm = Open(kit, "web"))
        {
            Min3();
            warm.Scheduler = scheduler;
            Min3();
        }

        var withoutDetail = Min3();

        long inline;
        using (var detail = Open(kit, "web"))
        {
            inline = Min3();
        }

        var history = new ScriptedHistory();
        long coalesced;
        using (var detail = Open(kit, "web", history: history))
        {
            detail.Scheduler = scheduler;
            var queriesAfterOpen = history.Queries;
            coalesced = Min3();
            Assert.Equal(queriesAfterOpen, history.Queries); // no per-tick query while the last success does not move
        }

        Assert.True(inline - withoutDetail <= tolerance, $"card alone {withoutDetail} B; with the Detail (inline flush) {inline} B / {cycles} cycles");
        Assert.True(coalesced - withoutDetail <= tolerance, $"card alone {withoutDetail} B; with the Detail (coalesced) {coalesced} B / {cycles} cycles");
    }

    // ---- H-UI5-3 CPU pulse ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePulse_IsTheLast30RealSamples_FromLocalHistory()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        var history = new ScriptedHistory();
        history.Next(Series(40, nullEvery: 7));
        using var detail = Open(kit, "web", history: history);

        Assert.Equal(1, history.Queries);
        Assert.Equal(HistoryTimeRange.LastHour, history.LastRange);
        Assert.Equal(30, detail.CpuPulseSamples.Count);
        Assert.True(detail.HasCpuPulse);
        Assert.Equal(40, detail.CpuPulseSamples[^1]); // newest last
        Assert.DoesNotContain(detail.CpuPulseSamples, value => value % 7 == 0); // unmeasured points are not drawn
    }

    [Fact]
    public async Task FewerSamples_DrawFewerBars_AndNoneDrawNone_NeverPadded()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3).Add("db", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        var history = new ScriptedHistory();
        history.Next(Series(5));
        using var web = Open(kit, "web", history: history);
        history.Next(HistorySeries.Empty);
        using var db = Open(kit, "db", history: history);

        Assert.Equal(new double[] { 1, 2, 3, 4, 5 }, web.CpuPulseSamples);
        Assert.Empty(db.CpuPulseSamples);
        Assert.False(db.HasCpuPulse);
    }

    [Fact]
    public async Task UnavailableHistory_IsNeverQueried_AndDrawsNoBars()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        var history = new ScriptedHistory { Available = false };
        using var detail = Open(kit, "web", history: history);

        Assert.Equal(0, history.Queries);
        Assert.Empty(detail.CpuPulseSamples);
    }

    [Fact]
    public async Task ThePulse_IsReadAgain_OnlyWhenTheLastSuccessMovedOneSamplingInterval()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        var history = new ScriptedHistory();
        using var detail = Open(kit, "web", history: history);
        var id = fleet.IdOf("web");
        var start = kit.States.Get(id).LastSuccessAt!.Value;
        Assert.Equal(1, history.Queries);

        kit.States.Set(kit.States.Get(id) with { LastSuccessAt = start.AddSeconds(10) });
        kit.States.Set(kit.States.Get(id) with { LastSuccessAt = start.AddSeconds(29) });
        Assert.Equal(1, history.Queries);

        kit.States.Set(kit.States.Get(id) with { LastSuccessAt = start.Add(HistorySamplingPolicy.DefaultMinInterval) });
        Assert.Equal(2, history.Queries);
    }

    [Fact]
    public async Task ASupersededRead_NeverOverwritesANewerOne()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = await LoadedAsync(fleet);
        var history = new ScriptedHistory();
        var slow = history.Pending();
        using var detail = Open(kit, "web", history: history);
        var id = fleet.IdOf("web");
        history.Next(Series(3));

        kit.States.Set(kit.States.Get(id) with { LastSuccessAt = Now.AddMinutes(5) });
        Assert.Equal(new double[] { 1, 2, 3 }, detail.CpuPulseSamples);

        slow.SetResult(Result(Series(30)));
        Assert.Equal(new double[] { 1, 2, 3 }, detail.CpuPulseSamples);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static async Task<Ui4TestKit.Harness> LoadedAsync(
        Ui4TestKit.Fleet fleet,
        ILocalizationService? localization = null,
        IServerDialogService? dialogs = null,
        IServerProfileService? profiles = null)
    {
        var kit = Ui4TestKit.Create(fleet, localization, dialogs: dialogs, profiles: profiles);
        await kit.Dashboard.LoadAsync();
        return kit;
    }

    private static ServerDetailViewModel Open(
        Ui4TestKit.Harness kit,
        string name,
        ServerDetailOrigin origin = ServerDetailOrigin.Overview,
        IServerHistoryQueryService? history = null,
        ServerDetailReturnFocus? returnFocus = null,
        PresentationClock? clock = null)
    {
        var detail = new ServerDetailViewModel(
            kit.Dashboard, kit.Navigation, kit.Localization, history, returnFocus, monitoringOptions: null,
            clock ?? new PresentationClock(new FakeTimeProvider(Now)));
        detail.Load(kit.Dashboard.VisibleServers.Single(card => card.Name == name).Server.Id, origin);
        return detail;
    }

    private static Func<Guid, Task<bool>> HideIn(Ui4TestKit.Harness kit) => id =>
    {
        var index = kit.Servers.Servers.FindIndex(server => server.Id == id);
        kit.Servers.Servers[index] = kit.Servers.Servers[index] with { IsHidden = true };
        kit.Servers.RaiseChanged();
        return Task.FromResult(true);
    };

    private static List<string?> Recorder(INotifyPropertyChanged source)
    {
        var raised = new List<string?>();
        source.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }

    private static Delegate[] Handlers(ObservableObject source) =>
        (typeof(ObservableObject).GetField(nameof(ObservableObject.PropertyChanged), BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(source) as Delegate)?.GetInvocationList() ?? [];

    private static int HandlerCount(ObservableObject source) => Handlers(source).Length;

    private static bool IsSubscribed(ObservableObject source, object subscriber) =>
        Handlers(source).Any(handler => ReferenceEquals(handler.Target, subscriber));

    private static HistorySeries Series(int count, int nullEvery = 0) => new()
    {
        Points = Enumerable.Range(1, count)
            .Select(i => new HistoryChartPoint
            {
                TimestampUtc = Now.AddSeconds(-30 * (count - i)),
                Value = nullEvery > 0 && i % nullEvery == 0 ? null : i
            })
            .ToList(),
        MaxConnectGap = TimeSpan.FromSeconds(90)
    };

    private static ServerHistoryResult Result(HistorySeries cpu) => new()
    {
        ServerId = Guid.Empty,
        Range = HistoryTimeRange.LastHour,
        StartUtc = Now.AddHours(-1),
        EndUtc = Now,
        Cpu = cpu,
        Memory = HistorySeries.Empty,
        Disk = HistorySeries.Empty
    };

    private sealed class ScriptedHistory : IServerHistoryQueryService
    {
        private readonly Queue<TaskCompletionSource<ServerHistoryResult>> _answers = new();

        public bool Available { get; set; } = true;

        public bool IsAvailable => Available;

        public int Queries { get; private set; }

        public HistoryTimeRange? LastRange { get; private set; }

        public void Next(HistorySeries cpu)
        {
            var answer = new TaskCompletionSource<ServerHistoryResult>();
            answer.SetResult(Result(cpu));
            _answers.Enqueue(answer);
        }

        public TaskCompletionSource<ServerHistoryResult> Pending()
        {
            var answer = new TaskCompletionSource<ServerHistoryResult>();
            _answers.Enqueue(answer);
            return answer;
        }

        public Task<ServerHistoryResult> GetHistoryAsync(Guid serverId, HistoryTimeRange range, CancellationToken cancellationToken = default)
        {
            Queries++;
            LastRange = range;
            return _answers.TryDequeue(out var answer) ? answer.Task : Task.FromResult(Result(HistorySeries.Empty));
        }
    }

    private sealed class RecordingDialogs : IServerDialogService
    {
        public List<Server> Edited { get; } = [];

        public List<Server> ConfirmedRemovals { get; } = [];

        public bool ConfirmRemove { get; init; }

        public Task<ServerEditorResult?> ShowEditorAsync(Server? server)
        {
            Edited.Add(server!);
            return Task.FromResult<ServerEditorResult?>(null);
        }

        public Task<ServerEditorResult?> ShowEditorForDiscoveryAsync(ServerDiscoveryPrefill prefill) => Task.FromResult<ServerEditorResult?>(null);

        public Task<ServerEditorResult?> ShowEditorForSshImportAsync() => Task.FromResult<ServerEditorResult?>(null);

        public Task<bool> ConfirmRemoveAsync(Server server)
        {
            ConfirmedRemovals.Add(server);
            return Task.FromResult(ConfirmRemove);
        }
    }

    private sealed class RemovingProfiles(Func<Guid, Ui4TestKit.Harness> kit) : IServerProfileService
    {
        public Task<ServerOperationResult> AddAsync(ServerProfileInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServerOperationResult> UpdateAsync(Server current, ServerProfileInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(Server server, CancellationToken cancellationToken = default)
        {
            var harness = kit(server.Id);
            harness.Servers.Servers.RemoveAll(candidate => candidate.Id == server.Id);
            harness.Servers.RaiseChanged();
            return Task.FromResult(true);
        }
    }
}
