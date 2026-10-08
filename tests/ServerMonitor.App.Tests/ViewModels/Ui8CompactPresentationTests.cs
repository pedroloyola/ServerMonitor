using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Windowing;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.8 SPEC §4 items 2, 3, 4, 7, 8: the Compact presentation is a view over the SAME dashboard cards (same instances,
/// same order), never shows an unknown / Offline metric as 0, keeps hidden servers out and tells all-hidden from empty,
/// leaks no handler across rebuilds / mode cycles / disposal, and raises nothing for an identical sample. Deterministic:
/// the UI.4 kit's real DashboardViewModel over in-memory stores, a fixed clock, the overview scheduler seam - no wall
/// clock, no dispatcher, no window.
/// </summary>
public sealed class Ui8CompactPresentationTests
{
    private static readonly string[] MetricProperties =
    [
        nameof(CompactServerRowViewModel.CpuText), nameof(CompactServerRowViewModel.IsCpuKnown), nameof(CompactServerRowViewModel.CpuBarValue), nameof(CompactServerRowViewModel.CpuSeverity),
        nameof(CompactServerRowViewModel.MemoryText), nameof(CompactServerRowViewModel.IsMemoryKnown), nameof(CompactServerRowViewModel.MemoryBarValue), nameof(CompactServerRowViewModel.MemorySeverity),
        nameof(CompactServerRowViewModel.DiskText), nameof(CompactServerRowViewModel.IsDiskKnown), nameof(CompactServerRowViewModel.DiskBarValue), nameof(CompactServerRowViewModel.DiskSeverity)
    ];

    // ---- §4.2 same source ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Rows_AreTheVisibleServersCards_ByReference_InTheSameOrder_EvenWhenCreatedAfterTheFirstLoad()
    {
        var harness = Ui4TestKit.Create(Mixed());
        await harness.Dashboard.LoadAsync();

        using var compact = Compact(harness);

        AssertSameSource(harness.Dashboard, compact);
        Assert.Equal(CompactBodyState.List, compact.BodyState);
    }

    [Fact]
    public async Task AReload_RebuildsFromServersReloaded_NeverFromTheCollectionReset()
    {
        var harness = Ui4TestKit.Create(Mixed());
        using var compact = Compact(harness);
        Assert.Empty(compact.Rows);
        Assert.Equal(CompactBodyState.Loading, compact.BodyState);

        var rowsDuringReset = new List<int>();
        harness.Dashboard.VisibleServers.CollectionChanged += (_, _) => rowsDuringReset.Add(compact.Rows.Count);
        var raised = Changes(compact);
        await harness.Dashboard.LoadAsync();

        // While the dashboard swaps its cards the compact list keeps the previous (empty) one: no row ever points at a
        // momentarily-empty collection; the rebuild happens once, at ServersReloaded.
        Assert.All(rowsDuringReset, count => Assert.Equal(0, count));
        Assert.Equal(1, raised.Count(name => name == nameof(CompactPresentationViewModel.Rows)));
        AssertSameSource(harness.Dashboard, compact);

        var first = compact.Rows;
        await harness.Dashboard.LoadAsync();
        AssertSameSource(harness.Dashboard, compact);
        Assert.All(first, old => Assert.DoesNotContain(old, compact.Rows));
    }

    // ---- §4.3 offline / unknown never 0 --------------------------------------------------------------------------

    [Fact]
    public async Task OfflineWithARetainedReading_AndUnknownMetrics_AreNeverZero_InTextBarOrAccessibleName()
    {
        var fleet = new Ui4TestKit.Fleet()
            .Add("off", ServerHealth.Offline, cpu: 30, mem: 40, disk: 50)   // retained snapshot the row must hide
            .Add("partial", ServerHealth.Healthy, cpu: 12, mem: null, disk: 51)
            .Add("never", ServerHealth.Unknown, snapshot: false);
        var harness = Ui4TestKit.Create(fleet);
        await harness.Dashboard.LoadAsync();
        using var compact = Compact(harness);

        var off = Row(compact, "off");
        Assert.All(new[] { (off.CpuText, off.IsCpuKnown, off.CpuBarValue), (off.MemoryText, off.IsMemoryKnown, off.MemoryBarValue), (off.DiskText, off.IsDiskKnown, off.DiskBarValue) },
            metric =>
            {
                Assert.Equal("ServerMetricUnavailable", metric.Item1);
                Assert.False(metric.Item2);
                Assert.Equal(0, metric.Item3);
            });
        Assert.Equal("ServerStatusOffline", off.StatusText);
        Assert.False(off.ShowsStaleCue); // Offline hides the reading: no stale cue on values that are not shown

        var partial = Row(compact, "partial");
        Assert.Equal("ServerMetricUnavailable", partial.MemoryText);
        Assert.False(partial.IsMemoryKnown);
        Assert.Equal("12%", partial.CpuText);

        var never = Row(compact, "never");
        Assert.Equal("ServerStatusUnknown", never.StatusText);
        Assert.False(never.IsCpuKnown || never.IsMemoryKnown || never.IsDiskKnown);
        Assert.Equal(["ServerMetricUnavailable", "ServerMetricUnavailable", "ServerMetricUnavailable"], new[] { never.CpuText, never.MemoryText, never.DiskText });
    }

    [Fact]
    public async Task TheAccessibleName_ReadsEveryMetric_WithItsCue_AndNeverARetainedOrZeroValue()
    {
        var harness = Ui4TestKit.Create(Mixed(), localization: new ResWLocalizationService("en-US"));
        await harness.Dashboard.LoadAsync();
        using var compact = Compact(harness);

        Assert.Equal("prod-db-01, Attention. CPU 46%, RAM 71%, disk 88% needs attention. Open details", Row(compact, "prod-db-01").AccessibleName);
        Assert.Equal("prod-web-01, Healthy. CPU 24%, RAM 62%, disk 48%. Open details", Row(compact, "prod-web-01").AccessibleName);
        var off = Row(compact, "backup-nas").AccessibleName;
        Assert.DoesNotContain("%", off, StringComparison.Ordinal);
        Assert.DoesNotContain("30", off, StringComparison.Ordinal);
        Assert.Equal("—", Row(compact, "backup-nas").CpuText);
    }

    [Fact]
    public async Task TheStatusCopy_IsServerStatusPresentation_NeverHealthDisplayName()
    {
        var harness = Ui4TestKit.Create(Mixed());
        await harness.Dashboard.LoadAsync();
        using var compact = Compact(harness);

        Assert.All(compact.Rows, row =>
        {
            Assert.Equal(ServerStatusPresentation.StatusKey(row.Card.Health), row.StatusText);
            Assert.DoesNotContain("ServerHealth", row.StatusText, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(typeof(CompactServerRowViewModel).GetProperties(), property => property.Name == "HealthDisplayName");
    }

    [Fact]
    public async Task MetricSeverity_UsesTheEnginesThresholds_AndCarriesATextCue()
    {
        var fleet = new Ui4TestKit.Fleet()
            .Add("db", ServerHealth.Warning, cpu: 46, mem: 71, disk: 88)
            .Add("hot", ServerHealth.Critical, cpu: 97, mem: 20, disk: 30);
        var harness = Ui4TestKit.Create(fleet, localization: new ResWLocalizationService("pt-PT"));
        await harness.Dashboard.LoadAsync();
        using var compact = Compact(harness);

        var db = Row(compact, "db");
        Assert.Equal((ServerHealth.Healthy, ServerHealth.Healthy, ServerHealth.Warning), (db.CpuSeverity, db.MemorySeverity, db.DiskSeverity));
        Assert.Contains("disco 88% em atenção", db.AccessibleName, StringComparison.Ordinal);
        Assert.Equal(88, db.DiskBarValue);
        var hot = Row(compact, "hot");
        Assert.Equal(ServerHealth.Critical, hot.CpuSeverity);
        Assert.Contains("CPU 97% crítico", hot.AccessibleName, StringComparison.Ordinal);

        // The same MonitoringOptions instance the engine is composed with decides: lower the disk limit, 88 is critical.
        var strict = new MonitoringOptions { Thresholds = MonitoringThresholds.Default with { DiskCritical = 85 } };
        using var strictCompact = new CompactPresentationViewModel(harness.Dashboard, harness.Localization, strict);
        Assert.Equal(ServerHealth.Critical, Row(strictCompact, "db").DiskSeverity);
    }

    [Fact]
    public async Task AStaleNotOfflineRow_KeepsItsValues_WithTheEngineAgeCue()
    {
        var fleet = new Ui4TestKit.Fleet().Add("old", ServerHealth.Healthy, cpu: 28, mem: 44, disk: 55);
        var harness = Ui4TestKit.Create(fleet);
        await harness.Dashboard.LoadAsync();
        using var compact = Compact(harness);
        var row = compact.Rows.Single();
        Assert.False(row.ShowsStaleCue);

        var id = fleet.IdOf("old");
        harness.States.Set(harness.States.Get(id) with
        {
            IsStale = true,
            LastSuccessAt = Ui4TestKit.Now.AddHours(-2),
            LastAttemptAt = Ui4TestKit.Now
        });

        Assert.True(row.ShowsStaleCue);
        Assert.Equal("28%", row.CpuText);
        Assert.Equal(row.Card.StaleAgeDisplay, row.StaleAgeText);
        Assert.NotNull(row.StaleAgeText);
    }

    // ---- §4.4 hidden ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Hidden_NeverAppear_AllHiddenIsNotEmpty_AndARestoreReappears()
    {
        var fleet = new Ui4TestKit.Fleet()
            .Add("a", ServerHealth.Healthy, cpu: 1, mem: 2, disk: 3, hidden: true)
            .Add("b", ServerHealth.Healthy, cpu: 1, mem: 2, disk: 3, hidden: true);
        var harness = Ui4TestKit.Create(fleet);
        await harness.Dashboard.LoadAsync();
        using var compact = Compact(harness);

        Assert.Empty(compact.Rows);
        Assert.Equal(CompactBodyState.AllHidden, compact.BodyState);
        Assert.False(compact.ShowsSummary);

        harness.Servers.Servers[0] = harness.Servers.Servers[0] with { IsHidden = false };
        await harness.Dashboard.LoadAsync();

        var row = Assert.Single(compact.Rows);
        Assert.Equal("a", row.Name);
        Assert.Equal(CompactBodyState.List, compact.BodyState);

        var empty = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        await empty.Dashboard.LoadAsync();
        using var none = Compact(empty);
        Assert.Equal(CompactBodyState.Empty, none.BodyState);
    }

    [Theory]
    [InlineData(true, true, true, true, CompactBodyState.Loading)]
    [InlineData(false, true, true, true, CompactBodyState.List)]
    [InlineData(false, false, true, true, CompactBodyState.ConfigurationUnavailable)]
    [InlineData(false, false, false, true, CompactBodyState.AllHidden)]
    [InlineData(false, false, false, false, CompactBodyState.Empty)]
    public void TheBodyState_IsDerivedFromTheExistingFlags(bool loading, bool visible, bool unavailable, bool allHidden, CompactBodyState expected) =>
        Assert.Equal(expected, CompactPresentationViewModel.Derive(loading, visible, unavailable, allHidden));

    [Fact]
    public async Task TheSummary_CountsTheVisibleServers_AndForwardsTheOverviewFreshness()
    {
        var harness = Ui4TestKit.Create(Mixed());
        await harness.Dashboard.LoadAsync();
        using var compact = Compact(harness);

        Assert.Equal("ServersSummaryTotalOther", compact.ServerCountDisplay);
        Assert.Equal(harness.Dashboard.UpdatedAgoDisplay, compact.UpdatedAgoDisplay);
        Assert.True(compact.HasUpdatedAgo);
        Assert.True(compact.ShowsSummary);
    }

    // ---- §4.7 no leak ------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisposingARow_DetachesItFromItsCard()
    {
        var harness = Ui4TestKit.Create(Mixed());
        await harness.Dashboard.LoadAsync();
        var card = harness.Dashboard.VisibleServers[0];
        var before = Subscribers(card);

        var row = new CompactServerRowViewModel(card, harness.Localization, MonitoringThresholds.Default);
        Assert.Equal(before + 1, Subscribers(card));
        row.Dispose();
        row.Dispose();

        Assert.Equal(before, Subscribers(card));
    }

    [Fact]
    public async Task KRebuilds_LeaveTheOldCardsWithoutSubscribers_AndTheCurrentOnesWithAConstantCount()
    {
        var harness = Ui4TestKit.Create(Mixed());
        await harness.Dashboard.LoadAsync();
        using var compact = Compact(harness);
        var perCard = Subscribers(harness.Dashboard.VisibleServers[0]);

        var old = new List<ServerCardViewModel>();
        for (var i = 0; i < 10; i++)
        {
            old.AddRange(harness.Dashboard.VisibleServers);
            await harness.Dashboard.LoadAsync();
        }

        Assert.All(old, card => Assert.Equal(0, Subscribers(card)));
        Assert.All(harness.Dashboard.VisibleServers, card => Assert.Equal(perCard, Subscribers(card)));
    }

    [Fact]
    public async Task DisposingThePresentation_RemovesBothDashboardSubscriptions_AndEveryRow()
    {
        var harness = Ui4TestKit.Create(Mixed());
        await harness.Dashboard.LoadAsync();
        var dashboardHandlers = Subscribers(harness.Dashboard);
        var reloadHandlers = ReloadSubscribers(harness.Dashboard);
        var cardHandlers = harness.Dashboard.VisibleServers.Select(Subscribers).ToList();

        var compact = Compact(harness);
        Assert.Equal(dashboardHandlers + 1, Subscribers(harness.Dashboard));
        Assert.Equal(reloadHandlers + 1, ReloadSubscribers(harness.Dashboard));
        compact.Dispose();

        Assert.Equal(dashboardHandlers, Subscribers(harness.Dashboard));
        Assert.Equal(reloadHandlers, ReloadSubscribers(harness.Dashboard));
        Assert.Equal(cardHandlers, harness.Dashboard.VisibleServers.Select(Subscribers));
        Assert.Empty(compact.Rows);
    }

    [Fact]
    public async Task KFullCompactCycles_KeepEveryHandlerCountConstant()
    {
        var harness = Ui4TestKit.Create(Mixed());
        await harness.Dashboard.LoadAsync();
        var coordinator = new WindowModeCoordinator(new RecordingPlacementAdapter(), new FakeWindowPlacementStore(), NullLogger<WindowModeCoordinator>.Instance);
        using var windowMode = new WindowModeViewModel(coordinator);
        using var compact = Compact(harness);
        coordinator.Initialize();
        var cards = harness.Dashboard.VisibleServers.Select(Subscribers).ToList();
        var dashboard = Subscribers(harness.Dashboard);
        var rows = compact.Rows;

        for (var i = 0; i < 20; i++)
        {
            coordinator.Toggle();
        }

        Assert.Equal(cards, harness.Dashboard.VisibleServers.Select(Subscribers));
        Assert.Equal(dashboard, Subscribers(harness.Dashboard));
        Assert.Same(rows, compact.Rows); // a mode switch rebuilds nothing
    }

    // ---- §4.8 coalescing / dedupe ------------------------------------------------------------------------------

    [Fact]
    public async Task AnIdenticalSample_RaisesNothingOnTheRow_WhileTheCardRaisesItsFullSet()
    {
        var harness = Ui4TestKit.Create(Mixed());
        await harness.Dashboard.LoadAsync();
        using var compact = Compact(harness);
        var row = compact.Rows[0];
        var cardRaised = Changes(row.Card);
        var rowRaised = Changes(row);

        harness.States.Set(harness.States.Get(row.ServerId));

        Assert.True(cardRaised.Count >= 30, $"precondition: the shared card raised {cardRaised.Count}");
        Assert.Empty(rowRaised);
    }

    [Fact]
    public async Task AChangedSample_RaisesOnlyWhatTheRowPresentsDifferently()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, cpu: 24, mem: 62, disk: 48);
        var harness = Ui4TestKit.Create(fleet, localization: new ResWLocalizationService("en-US"));
        await harness.Dashboard.LoadAsync();
        using var compact = Compact(harness);
        var row = compact.Rows.Single();
        var raised = Changes(row);
        var id = fleet.IdOf("web");

        harness.Metrics.Snapshots[id] = harness.Metrics.Snapshots[id] with { CpuUsagePercent = 27 };
        harness.States.Set(harness.States.Get(id));

        Assert.Equal(
            [nameof(CompactServerRowViewModel.AccessibleName), nameof(CompactServerRowViewModel.CpuBarValue), nameof(CompactServerRowViewModel.CpuText)],
            raised.Order(StringComparer.Ordinal));
        Assert.Equal("27%", row.CpuText);
    }

    /// <summary>
    /// Atlas c1 A-2: two bursts of samples, each coalesced by the dashboard into ONE recompute, each visibly updating the
    /// Compact summary. Real en-US strings keep the seconds in the text (8 s → 1 s → 2 s, all distinct); the recorded
    /// notifications are cleared per phase: none before a flush, exactly one summary change after it; the rows are the
    /// same instances and nothing else is left queued.
    /// </summary>
    [Fact]
    public async Task TwoBursts_EachRecomputeTheSummaryOnce_AndTheCompactViewAddsNoScheduler()
    {
        var fleet = Mixed();
        var harness = Ui4TestKit.Create(fleet, localization: new ResWLocalizationService("en-US"));
        var queued = new List<Action>();
        harness.Dashboard.OverviewScheduler = action => { queued.Add(action); return true; };
        await harness.Dashboard.LoadAsync();
        using var compact = Compact(harness);
        var rows = compact.Rows;
        var raised = Changes(compact);
        queued.Clear();
        var shown = new List<string?> { compact.UpdatedAgoDisplay };

        for (var burst = 1; burst <= 2; burst++)
        {
            raised.Clear();
            foreach (var entry in fleet.Entries.Where(e => !e.Server.IsHidden))
            {
                harness.States.Set(harness.States.Get(entry.Server.Id) with { LastSuccessAt = Ui4TestKit.Now.AddSeconds(-burst) });
            }

            var pending = Assert.Single(queued); // the WHOLE burst queued one recompute
            queued.Clear();
            Assert.Empty(raised); // nothing before the flush
            pending();

            Assert.Equal(
                [nameof(CompactPresentationViewModel.HasUpdatedAgo), nameof(CompactPresentationViewModel.UpdatedAgoDisplay)],
                raised.Order(StringComparer.Ordinal));
            Assert.Equal(harness.Dashboard.UpdatedAgoDisplay, compact.UpdatedAgoDisplay);
            Assert.Contains(burst.ToString(CultureInfo.InvariantCulture), compact.UpdatedAgoDisplay, StringComparison.Ordinal);
            Assert.DoesNotContain(compact.UpdatedAgoDisplay, shown); // a new text each burst
            shown.Add(compact.UpdatedAgoDisplay);
            Assert.Same(rows, compact.Rows);
            Assert.Empty(queued); // the flush left nothing else pending
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private static Ui4TestKit.Fleet Mixed() => new Ui4TestKit.Fleet()
        .Add("prod-web-01", ServerHealth.Healthy, cpu: 24, mem: 62, disk: 48)
        .Add("prod-db-01", ServerHealth.Warning, cpu: 46, mem: 71, disk: 88)
        .Add("staging-01", ServerHealth.Healthy, cpu: 8, mem: 34, disk: 28)
        .Add("hidden-01", ServerHealth.Healthy, cpu: 8, mem: 34, disk: 28, hidden: true)
        .Add("backup-nas", ServerHealth.Offline, cpu: 30, mem: 40, disk: 50);

    private static CompactPresentationViewModel Compact(Ui4TestKit.Harness harness) =>
        new(harness.Dashboard, harness.Localization, MonitoringOptions.Default);

    private static CompactServerRowViewModel Row(CompactPresentationViewModel compact, string name) =>
        compact.Rows.Single(row => row.Name == name);

    private static void AssertSameSource(DashboardViewModel dashboard, CompactPresentationViewModel compact)
    {
        Assert.Equal(dashboard.VisibleServers.Count, compact.Rows.Count);
        for (var i = 0; i < compact.Rows.Count; i++)
        {
            Assert.Same(dashboard.VisibleServers[i], compact.Rows[i].Card);
        }
    }

    private static List<string> Changes(INotifyPropertyChanged source)
    {
        var names = new List<string>();
        source.PropertyChanged += (_, e) => names.Add(e.PropertyName ?? string.Empty);
        return names;
    }

    // The invocation list behind ObservableObject.PropertyChanged (a field-like event).
    private static int Subscribers(ObservableObject source) =>
        (typeof(ObservableObject).GetField(nameof(ObservableObject.PropertyChanged), BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(source) as Delegate)?.GetInvocationList().Length ?? 0;

    private static int ReloadSubscribers(DashboardViewModel dashboard) =>
        (typeof(DashboardViewModel).GetField(nameof(DashboardViewModel.ServersReloaded), BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(dashboard) as Delegate)?.GetInvocationList().Length ?? 0;
}
