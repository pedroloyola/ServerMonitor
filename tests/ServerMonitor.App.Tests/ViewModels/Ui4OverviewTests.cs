using System.Runtime.CompilerServices;
using ServerMonitor.App.Converters;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.4 §7 (phase 1): the overview's pure presentation rules and the dashboard view model that feeds them — health
/// counts and segments, the D-UI4-PRIORITY rule against the engine's own thresholds, search, "—" for unknown metrics,
/// loading, "Atualizado há", the widget deep-link to the interim page and the copy in the three cultures.
/// </summary>
public sealed class Ui4OverviewTests
{
    // ---- health aggregate ------------------------------------------------------------------------------------

    [Fact]
    public void HealthSummary_CountsEveryEngineState()
    {
        var summary = HealthSummary.From(
        [
            ServerHealth.Healthy, ServerHealth.Healthy, ServerHealth.Warning, ServerHealth.Critical,
            ServerHealth.Offline, ServerHealth.Unknown, ServerHealth.Healthy
        ]);

        Assert.Equal(new HealthSummary(7, 3, 1, 1, 1, 1), summary);
        Assert.Equal(HealthSummary.Empty, HealthSummary.From([]));
    }

    [Fact]
    public void Segments_AreOnePerServer_UpToTheCap_InSeverityOrder()
    {
        var summary = HealthSummary.From([ServerHealth.Offline, ServerHealth.Healthy, ServerHealth.Warning, ServerHealth.Healthy]);

        var segments = OverviewPresentation.Segments(summary);

        Assert.Equal(
            [ServerHealth.Healthy, ServerHealth.Healthy, ServerHealth.Warning, ServerHealth.Offline],
            segments.Select(segment => segment.Health));
        Assert.All(segments, segment => Assert.Equal(1, segment.Weight));
    }

    [Fact]
    public void Segments_AboveTheCap_AreOneProportionalSegmentPerPresentState()
    {
        var healths = Enumerable.Repeat(ServerHealth.Healthy, 480)
            .Concat(Enumerable.Repeat(ServerHealth.Warning, 12))
            .Concat(Enumerable.Repeat(ServerHealth.Offline, 8))
            .ToList();

        var segments = OverviewPresentation.Segments(HealthSummary.From(healths));

        Assert.Equal(3, segments.Count);
        Assert.Equal(500, segments.Sum(segment => segment.Weight));
        Assert.Equal([480, 12, 8], segments.Select(segment => segment.Weight));
        Assert.Equal(OverviewPresentation.MaxDiscreteHealthSegments,
            OverviewPresentation.Segments(HealthSummary.From(Enumerable.Repeat(ServerHealth.Healthy, OverviewPresentation.MaxDiscreteHealthSegments))).Count);
    }

    [Theory]
    [InlineData(ServerHealth.Healthy, SaStatusKind.Healthy)]
    [InlineData(ServerHealth.Warning, SaStatusKind.Attention)]
    [InlineData(ServerHealth.Critical, SaStatusKind.Error)]
    [InlineData(ServerHealth.Offline, SaStatusKind.Offline)]
    [InlineData(ServerHealth.Unknown, SaStatusKind.Unknown)]
    public void StateMapping_FollowsTheEngineHealth(ServerHealth health, SaStatusKind expected) =>
        Assert.Equal(expected, ServerHealthToStatusKindConverter.Map(health));

    // ---- D-UI4-PRIORITY ----------------------------------------------------------------------------------------

    private static readonly PriorityProblemSelector Engine = new(MonitoringThresholds.Default);

    private static PriorityServerInput Input(string name, ServerHealth health, double? cpu = null, double? mem = null, double? disk = null, bool snapshot = true) =>
        new(Guid.NewGuid(), name, health, snapshot
            ? new ServerMetricsSnapshot { ServerId = Guid.Empty, CollectedAt = Ui4TestKit.Now, CpuUsagePercent = cpu, MemoryUsagePercent = mem, DiskUsagePercent = disk }
            : null);

    [Fact]
    public void Priority_CriticalBeatsAHigherWarningPercentage()
    {
        var problem = Engine.Select(
        [
            Input("cpu-warn", ServerHealth.Warning, cpu: 94),
            Input("disk-crit", ServerHealth.Critical, disk: 90)
        ]);

        Assert.NotNull(problem);
        Assert.Equal("disk-crit", problem.ServerName);
        Assert.Equal(PriorityMetric.Disk, problem.Metric);
        Assert.Equal(ServerHealth.Critical, problem.Severity);
        Assert.Equal(90, problem.Percent);
    }

    [Fact]
    public void Priority_SameSeverity_HigherPercentageWins_ThenListOrder()
    {
        Assert.Equal("b", Engine.Select([Input("a", ServerHealth.Warning, mem: 85), Input("b", ServerHealth.Warning, cpu: 88)])!.ServerName);

        // Exact tie: the server earlier in the list wins, whatever the metric.
        var tie = Engine.Select([Input("first", ServerHealth.Warning, disk: 86), Input("second", ServerHealth.Warning, cpu: 86)]);
        Assert.Equal("first", tie!.ServerName);

        // Tie inside one server: metric order CPU, memory, disk.
        Assert.Equal(PriorityMetric.Cpu, Engine.Select([Input("one", ServerHealth.Warning, cpu: 82, mem: 82, disk: 82)])!.Metric);
    }

    [Fact]
    public void Priority_LimitsAreInclusive_AndTheSameAsTheEngines()
    {
        var thresholds = MonitoringThresholds.Default;
        foreach (var (metric, warning, critical) in new[]
                 {
                     (PriorityMetric.Cpu, thresholds.CpuWarning, thresholds.CpuCritical),
                     (PriorityMetric.Memory, thresholds.MemoryWarning, thresholds.MemoryCritical),
                     (PriorityMetric.Disk, thresholds.DiskWarning, thresholds.DiskCritical)
                 })
        {
            foreach (var percent in new[] { warning - 0.01, warning, critical - 0.01, critical, 100d })
            {
                var input = metric switch
                {
                    PriorityMetric.Cpu => Input("s", ServerHealth.Warning, cpu: percent),
                    PriorityMetric.Memory => Input("s", ServerHealth.Warning, mem: percent),
                    _ => Input("s", ServerHealth.Warning, disk: percent)
                };
                var engine = HealthEvaluator.EvaluateFromMetrics(input.Snapshot, thresholds);
                var problem = Engine.Select([input]);

                if (engine is ServerHealth.Healthy)
                {
                    Assert.Null(problem);
                }
                else
                {
                    Assert.NotNull(problem);
                    Assert.Equal(engine, problem.Severity);
                    Assert.Equal(metric, problem.Metric);
                }
            }
        }
    }

    [Fact]
    public void Priority_UsesTheInjectedThresholds()
    {
        var input = Input("s", ServerHealth.Warning, cpu: 70);

        Assert.Null(Engine.Select([input]));
        var custom = new PriorityProblemSelector(MonitoringThresholds.Default with { CpuWarning = 60, CpuCritical = 65 });
        Assert.Equal(ServerHealth.Critical, custom.Select([input])!.Severity);
    }

    [Fact]
    public void Priority_NothingIsFabricated_OfflineUnknownHealthyAndNoSnapshotAreExcluded()
    {
        Assert.Null(Engine.Select(
        [
            Input("offline-with-old-data", ServerHealth.Offline, disk: 99),
            Input("unknown", ServerHealth.Unknown, cpu: 99),
            Input("healthy-label", ServerHealth.Healthy, mem: 99),
            Input("warning-without-snapshot", ServerHealth.Warning, snapshot: false),
            Input("warning-unknown-metrics", ServerHealth.Warning, cpu: null, mem: double.NaN)
        ]));
        Assert.Null(Engine.Select([]));
    }

    // ---- search + address ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("db", true)]
    [InlineData("PROD-DB", true)]
    [InlineData("10.0.4", true)]
    [InlineData("10.0.4.12:2222", true)]
    [InlineData(" web ", false)]
    [InlineData("nas", false)]
    public void Search_MatchesNameOrAddress_PartialAndCaseInsensitive(string? query, bool expected) =>
        Assert.Equal(expected, OverviewPresentation.MatchesSearch("prod-db-01", "10.0.4.12", 2222, query));

    [Fact]
    public void Address_ShowsThePortOnlyWhenNotTheDefault()
    {
        Assert.Equal("prod-web-01.local", OverviewPresentation.Address("prod-web-01.local", 22));
        Assert.Equal("staging.local:2222", OverviewPresentation.Address("staging.local", 2222));
        Assert.Equal("[fe80::1]:2200", OverviewPresentation.Address("fe80::1", 2200));
    }

    // ---- dashboard view model ------------------------------------------------------------------------------------

    private static Ui4TestKit.Fleet FigmaFleet() => new Ui4TestKit.Fleet()
        .Add("prod-web-01", ServerHealth.Healthy, 22, 41, 52)
        .Add("prod-db-01", ServerHealth.Warning, 46, 71, 88)
        .Add("staging-01", ServerHealth.Healthy, 12, 38, 44)
        .Add("docker-host", ServerHealth.Healthy, 47, 58, 66)
        .Add("mac-mini", ServerHealth.Healthy, 9, 52, 39)
        .Add("backup-nas", ServerHealth.Offline, snapshot: false)
        .Add("old-build", ServerHealth.Healthy, 1, 1, 1, hidden: true);

    [Fact]
    public async Task Overview_ComputesHealthPriorityAndFreshness_FromTheCards()
    {
        var kit = Ui4TestKit.Create(FigmaFleet(), new ResWLocalizationService("pt-PT"));
        var vm = kit.Dashboard;

        Assert.True(vm.IsLoading);
        await vm.LoadAsync();

        Assert.False(vm.IsLoading);
        Assert.Equal(new HealthSummary(6, 4, 1, 0, 1, 0), vm.HealthSummary);
        Assert.Equal(1, vm.HiddenServerCount);
        Assert.Equal("4", vm.HealthyCountDisplay);
        Assert.Equal("de 6 saudáveis", vm.HealthOfTotalDisplay);
        Assert.Equal(["1 atenção", "1 sem ligação"], vm.HealthChips.Select(chip => chip.Text));
        Assert.Equal("4 de 6 servidores saudáveis, 1 em atenção, 1 sem ligação", vm.HealthAutomationName);
        Assert.Equal(6, vm.HealthSegments.Count);

        Assert.True(vm.HasPriorityProblem);
        Assert.False(vm.ShowNoProblems);
        Assert.Equal("Disco quase cheio", vm.PriorityTitle);
        Assert.Equal("88%", vm.PriorityPercentDisplay);
        Assert.Equal("prod-db-01", vm.PriorityServerName);
        Assert.Equal("Disco quase cheio: 88% em prod-db-01, Atenção. Abrir servidor.", vm.PriorityAutomationName);

        Assert.Equal("Atualizado há 8 s", vm.UpdatedAgoDisplay);
        Assert.True(vm.ShowOverviewContent);
        Assert.False(vm.ShowEmptyState);
    }

    [Fact]
    public async Task Overview_CriticalIsCountedAndChipped_OnlyWhenPresent()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet()
            .Add("a", ServerHealth.Critical, disk: 95)
            .Add("b", ServerHealth.Critical, cpu: 99)
            .Add("c", ServerHealth.Unknown, snapshot: false), new ResWLocalizationService("en-US"));

        await kit.Dashboard.LoadAsync();

        Assert.Equal(["2 critical", "1 no data"], kit.Dashboard.HealthChips.Select(chip => chip.Text));
        Assert.Equal("0 of 3 servers healthy, 2 critical, 1 without data", kit.Dashboard.HealthAutomationName);
        Assert.Equal("b", kit.Dashboard.PriorityServerName);
        Assert.Equal(PriorityMetric.Cpu, kit.Dashboard.PriorityProblem!.Metric);
    }

    [Fact]
    public async Task Overview_WithoutCandidates_ShowsTheNeutralNoProblemsState()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet()
            .Add("web", ServerHealth.Healthy, 10, 10, 10)
            .Add("gone", ServerHealth.Offline, disk: 99));

        await kit.Dashboard.LoadAsync();

        Assert.Null(kit.Dashboard.PriorityProblem);
        Assert.True(kit.Dashboard.ShowNoProblems);
    }

    /// <summary>Cortex r1 NIT-3: "Sem problemas" never shows next to a Warning/Critical count, even when no metric
    /// candidate exists (here: attention reported without a snapshot).</summary>
    [Fact]
    public async Task NoProblems_IsNeverShown_WhileAnyServerIsInAttentionOrCritical()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet()
            .Add("web", ServerHealth.Healthy, 10, 10, 10)
            .Add("odd", ServerHealth.Warning, snapshot: false));

        await kit.Dashboard.LoadAsync();

        Assert.Null(kit.Dashboard.PriorityProblem);
        Assert.False(kit.Dashboard.ShowNoProblems);
        Assert.Equal(1, kit.Dashboard.HealthSummary.Warning);
    }

    [Fact]
    public async Task Overview_NothingCollectedYet_HidesFreshness_AndEmptyListShowsTheEmptyState()
    {
        var unavailable = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("new", ServerHealth.Unknown, snapshot: false));
        await unavailable.Dashboard.LoadAsync();
        Assert.Null(unavailable.Dashboard.UpdatedAgoDisplay);
        Assert.False(unavailable.Dashboard.HasUpdatedAgo);

        var empty = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        await empty.Dashboard.LoadAsync();
        Assert.True(empty.Dashboard.ShowEmptyState);
        Assert.False(empty.Dashboard.ShowOverviewContent);
        Assert.False(empty.Dashboard.ShowNoProblems);
        Assert.Equal(HealthSummary.Empty, empty.Dashboard.HealthSummary);
    }

    [Fact]
    public async Task IsLoading_StaysTrueWhileTheFirstLoadIsPending_AndNeverShowsTheEmptyState()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        var gate = new TaskCompletionSource<IReadOnlyList<Server>>();
        kit.Servers.GetAllOverride = _ => gate.Task;

        var load = kit.Dashboard.LoadAsync();

        Assert.True(kit.Dashboard.IsLoading);
        Assert.False(kit.Dashboard.ShowEmptyState);
        Assert.False(kit.Dashboard.ShowOverviewContent);
        gate.SetResult([]);
        await load;
        Assert.False(kit.Dashboard.IsLoading);
        Assert.True(kit.Dashboard.ShowEmptyState);
    }

    [Fact]
    public async Task IsLoading_IsNullSafe_OnAConstructorlessInstance()
    {
        // The pitfall: contract tests build the dashboard with GetUninitializedObject and still load it.
        var vm = (DashboardViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DashboardViewModel));
        Set(vm, "_serverService", new FakeServerService());
        Set(vm, "_configuredEndpoints", new HashSet<string>(StringComparer.Ordinal));
        Set(vm, "<VisibleServers>k__BackingField", new System.Collections.ObjectModel.ObservableCollection<ServerCardViewModel>());
        Set(vm, "<DiscoveredServers>k__BackingField", new System.Collections.ObjectModel.ObservableCollection<DiscoveredServerViewModel>());
        Set(vm, "_discoveryService", new EmptyDiscovery());

        Assert.False(vm.IsLoading);
        await vm.LoadAsync();

        Assert.False(vm.IsLoading);
        Assert.Equal(HealthSummary.Empty, vm.HealthSummary);
        Assert.Empty(vm.HealthSegments);
        Assert.Empty(vm.OverviewServers);
        Assert.Null(vm.PriorityProblem);
        Assert.NotNull(vm.HealthAutomationName);
        Assert.False(vm.RefreshAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task Freshness_FollowsTheD_UI3_9Buckets()
    {
        foreach (var (age, expected) in new[]
                 {
                     (TimeSpan.FromMilliseconds(400), "Atualizado agora mesmo"),
                     (TimeSpan.FromSeconds(59), "Atualizado há 59 s"),
                     (TimeSpan.FromMinutes(5), "Atualizado há 5 min"),
                     (TimeSpan.FromHours(3), "Atualizado há 3 h"),
                     (TimeSpan.FromDays(2), "Atualizado há 2 d")
                 })
        {
            var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet()
                .Add("a", ServerHealth.Healthy, 1, 1, 1, lastSuccess: Ui4TestKit.Now - age - TimeSpan.FromHours(1))
                .Add("b", ServerHealth.Healthy, 1, 1, 1, lastSuccess: Ui4TestKit.Now - age), new ResWLocalizationService("pt-PT"));
            await kit.Dashboard.LoadAsync();
            Assert.Equal(expected, kit.Dashboard.UpdatedAgoDisplay);
        }
    }

    [Fact]
    public async Task EngineStateChange_RecomputesTheOverview()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 10, 10, 10);
        var kit = Ui4TestKit.Create(fleet);
        await kit.Dashboard.LoadAsync();
        Assert.Null(kit.Dashboard.PriorityProblem);

        var id = fleet.IdOf("web");
        kit.Metrics.Snapshots[id] = kit.Metrics.Snapshots[id] with { DiskUsagePercent = 91 };
        kit.States.Set(kit.States.Get(id) with { Health = ServerHealth.Critical });

        Assert.Equal(new HealthSummary(1, 0, 0, 1, 0, 0), kit.Dashboard.HealthSummary);
        Assert.Equal(PriorityMetric.Disk, kit.Dashboard.PriorityProblem!.Metric);
    }

    [Fact]
    public async Task OverviewList_IsSearchable_LimitedAndCountsTheRest()
    {
        var fleet = new Ui4TestKit.Fleet();
        for (var i = 0; i < 20; i++)
        {
            fleet.Add($"node-{i:00}", ServerHealth.Healthy, 1, 1, 1);
        }

        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();

        Assert.Equal(OverviewPresentation.OverviewListLimit, kit.Dashboard.OverviewServers.Count);
        Assert.Equal(20 - OverviewPresentation.OverviewListLimit, kit.Dashboard.OverviewMoreCount);

        kit.Dashboard.OverviewSearchText = "NODE-1";
        Assert.Equal(OverviewPresentation.OverviewListLimit, kit.Dashboard.OverviewServers.Count);
        Assert.Equal(2, kit.Dashboard.OverviewMoreCount);

        kit.Dashboard.OverviewSearchText = "zzz";
        Assert.Empty(kit.Dashboard.OverviewServers);
        Assert.True(kit.Dashboard.HasOverviewSearchNoResults);
        Assert.Equal("Nenhum servidor encontrado", kit.Dashboard.OverviewNoResultsTitle);
        Assert.Equal("Não há servidores com o nome ou endereço “zzz”. Experimenta outra pesquisa.", kit.Dashboard.OverviewNoResultsMessage);

        kit.Dashboard.ClearOverviewSearchCommand.Execute(null);
        Assert.Equal(string.Empty, kit.Dashboard.OverviewSearchText);
        Assert.False(kit.Dashboard.HasOverviewSearchNoResults);
    }

    [Fact]
    public async Task Navigation_ViewAll_RowAndPriorityOpenTheRightPages()
    {
        var fleet = FigmaFleet();
        var kit = Ui4TestKit.Create(fleet);
        await kit.Dashboard.LoadAsync();

        kit.Dashboard.OpenServerDirectoryCommand.Execute(null);
        Assert.Equal(1, kit.Navigation.ServersCount);

        kit.Dashboard.OverviewServers[0].OpenDetailCommand.Execute(null);
        kit.Dashboard.OpenPriorityProblemCommand.Execute(null);
        Assert.Equal(
            [(fleet.IdOf("prod-web-01"), ServerDetailOrigin.Overview), (fleet.IdOf("prod-db-01"), ServerDetailOrigin.Overview)],
            kit.Navigation.ServerDetailRequests);
    }

    [Fact]
    public async Task WidgetDeepLink_OpensTheInterimPage_RetriesUntilLoaded_AndIgnoresARemovedServer()
    {
        var fleet = FigmaFleet();
        var kit = Ui4TestKit.Create(fleet);
        var target = fleet.IdOf("mac-mini");

        // Before the first load: pending, nothing opened yet.
        kit.Dashboard.FocusServer(target);
        Assert.Empty(kit.Navigation.ServerDetailRequests);

        await kit.Dashboard.LoadAsync();
        Assert.Equal([(target, ServerDetailOrigin.Overview)], kit.Navigation.ServerDetailRequests);

        // Resolved once: a later reload does not reopen it.
        await kit.Dashboard.LoadAsync();
        Assert.Single(kit.Navigation.ServerDetailRequests);

        // A removed (unknown) server never resolves: the app stays on the overview, no error.
        kit.Dashboard.FocusServer(Guid.NewGuid());
        await kit.Dashboard.LoadAsync();
        Assert.Single(kit.Navigation.ServerDetailRequests);
        Assert.False(kit.Dashboard.IsOperationErrorOpen);

        // A dashboard intent supersedes a pending server intent.
        kit.Dashboard.ClearServerFocus();
        kit.Servers.Servers.RemoveAll(server => server.Id == target);
        await kit.Dashboard.LoadAsync();
        kit.Dashboard.FocusServer(target);
        kit.Dashboard.ClearServerFocus();
        kit.Servers.Servers.Add(fleet.Entries.Single(entry => entry.Server.Id == target).Server);
        await kit.Dashboard.LoadAsync();
        Assert.Single(kit.Navigation.ServerDetailRequests);
    }

    // ---- rows ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rows_ShowADashForUnknownMetrics_NeverZero_AndOfflineHidesStaleValues()
    {
        var fleet = new Ui4TestKit.Fleet()
            .Add("partial", ServerHealth.Healthy, cpu: 12, mem: null, disk: 0, port: 2222)
            .Add("nodata", ServerHealth.Unknown, snapshot: false)
            .Add("offline-stale", ServerHealth.Offline, cpu: 40, mem: 50, disk: 60);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();
        var rows = kit.Dashboard.OverviewServers;

        Assert.Equal(("12%", "—", "0%"), (rows[0].CpuDisplay, rows[0].MemoryDisplay, rows[0].DiskDisplay));
        Assert.Equal("partial.local:2222", rows[0].Address);
        Assert.Equal(("—", "—", "—"), (rows[1].CpuDisplay, rows[1].MemoryDisplay, rows[1].DiskDisplay));
        Assert.Equal(("—", "—", "—"), (rows[2].CpuDisplay, rows[2].MemoryDisplay, rows[2].DiskDisplay));
        Assert.All(rows.Skip(1), row => Assert.DoesNotContain("0%", row.CpuDisplay + row.MemoryDisplay + row.DiskDisplay));
        Assert.Equal("Sem ligação", rows[2].StatusDisplay);
        Assert.Equal("Ver detalhe de offline-stale", rows[2].DetailAutomationName);
        Assert.Equal("nodata, Sem dados, CPU sem dados, RAM sem dados, disco sem dados, nodata.local, Linux", rows[1].RowAutomationName);
    }

    [Fact]
    public async Task Rows_FollowTheEngineState()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 10, 20, 30);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("en-US"));
        await kit.Dashboard.LoadAsync();
        var row = kit.Dashboard.OverviewServers.Single();
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        kit.States.Set(kit.States.Get(fleet.IdOf("web")) with { Health = ServerHealth.Offline });

        Assert.Equal("No connection", row.StatusDisplay);
        Assert.Equal("—", row.CpuDisplay);
        Assert.NotEmpty(raised);
    }

    // ---- Servidores directory ---------------------------------------------------------------------------------

    [Fact]
    public async Task Directory_SummarySearchNoResultsAndHiddenNote()
    {
        var kit = Ui4TestKit.Create(FigmaFleet(), new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();
        using var vm = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());

        Assert.Equal("6 servidores · 4 saudáveis · 1 atenção · 1 sem ligação", vm.SummaryDisplay);
        Assert.Equal(6, vm.Rows.Count);
        Assert.True(vm.ShowTable);
        Assert.True(vm.ShowHiddenServersNote);

        vm.SearchText = "PROD";
        Assert.Equal(["prod-web-01", "prod-db-01"], vm.Rows.Select(row => row.Name));
        vm.SearchText = "backup-nas.local";
        Assert.Equal("backup-nas", Assert.Single(vm.Rows).Name);

        vm.SearchText = "nope";
        Assert.Empty(vm.Rows);
        Assert.True(vm.HasNoResults);
        Assert.False(vm.ShowTable);
        Assert.Equal("Nenhum servidor encontrado", vm.NoResultsTitle);
        Assert.Contains("“nope”", vm.NoResultsMessage, StringComparison.Ordinal);
        Assert.False(vm.ShowHiddenServersNote);
        vm.ClearSearchCommand.Execute(null);
        Assert.Equal(6, vm.Rows.Count);
        Assert.False(vm.HasNoResults);

        vm.Rows[1].OpenDetailCommand.Execute(null);
        Assert.Equal(ServerDetailOrigin.Servers, kit.Navigation.ServerDetailRequests.Single().Origin);
        vm.BackToOverviewCommand.Execute(null);
        Assert.Equal(1, kit.Navigation.DashboardCount);
    }

    /// <summary>Prism r2 R2-B2 (Figma 112:14077): with no server the summary is only the total.</summary>
    [Theory]
    [InlineData("pt-PT", "0 servidores")]
    [InlineData("pt-BR", "0 servidores")]
    [InlineData("en-US", "0 servers")]
    public async Task Directory_WithNoServers_SummaryIsOnlyTheTotal(string culture, string expected)
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet(), new ResWLocalizationService(culture));
        await kit.Dashboard.LoadAsync();
        using var vm = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());

        Assert.Equal(expected, vm.SummaryDisplay);
        Assert.Equal(expected, vm.HeaderContextDisplay);
    }

    [Fact]
    public async Task Directory_SingularSummary_EmptyState_AndNoHiddenNote()
    {
        var one = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("solo", ServerHealth.Healthy, 1, 1, 1), new ResWLocalizationService("en-US"));
        await one.Dashboard.LoadAsync();
        using (var vm = new ServersViewModel(one.Dashboard, one.Navigation, one.Localization, TestClock.Fake()))
        {
            Assert.Equal("1 server · 1 healthy", vm.SummaryDisplay);
            Assert.True(vm.ShowHiddenServersNote); // Prism r1 (c): always under a table with rows
        }

        var empty = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        await empty.Dashboard.LoadAsync();
        using var emptyVm = new ServersViewModel(empty.Dashboard, empty.Navigation, empty.Localization, TestClock.Fake());
        Assert.True(emptyVm.ShowEmptyState);
        Assert.False(emptyVm.ShowHiddenServersNote);
        Assert.False(emptyVm.HasNoResults);
        emptyVm.SearchText = "x";
        Assert.False(emptyVm.HasNoResults);
    }

    [Fact]
    public async Task Directory_500Servers_FiltersWithoutLosingRows_AndRebuildsOnlyOnReload()
    {
        var fleet = new Ui4TestKit.Fleet();
        for (var i = 1; i <= 500; i++)
        {
            fleet.Add($"node-{i:000}", i % 29 == 0 ? ServerHealth.Offline : ServerHealth.Healthy, 10, 20, 30);
        }

        var kit = Ui4TestKit.Create(fleet);
        await kit.Dashboard.LoadAsync();
        using var vm = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());
        var rowsChanged = 0;
        vm.PropertyChanged += (_, e) => rowsChanged += e.PropertyName == nameof(ServersViewModel.Rows) ? 1 : 0;

        Assert.Equal(500, vm.Rows.Count);
        vm.SearchText = "node-4";
        Assert.Equal(100, vm.Rows.Count);
        Assert.Equal(1, rowsChanged);

        await kit.Dashboard.LoadAsync();
        Assert.Equal(2, rowsChanged);
        Assert.Equal(100, vm.Rows.Count);
    }

    [Fact]
    public async Task Directory_Dispose_StopsFollowingTheDashboard()
    {
        var kit = Ui4TestKit.Create(FigmaFleet());
        await kit.Dashboard.LoadAsync();
        var vm = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());
        var row = vm.Rows[0];
        vm.Dispose();
        var raised = 0;
        vm.PropertyChanged += (_, _) => raised++;
        row.PropertyChanged += (_, _) => raised++;

        // The SAME card changes (row + summary would follow), then the list is rebuilt (rows would be rebuilt).
        kit.States.Set(kit.States.Get(row.ServerId) with { Health = ServerHealth.Critical });
        await kit.Dashboard.LoadAsync();

        Assert.Equal(0, raised);
    }

    // ---- interim server page ----------------------------------------------------------------------------------

    [Fact]
    public async Task InterimPage_HostsTheLiveCard_RebindsAfterEdit_AndReturnsToTheOriginWhenTheServerGoes()
    {
        var fleet = FigmaFleet();
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();
        var id = fleet.IdOf("prod-db-01");
        using var vm = new ServerDetailViewModel(kit.Dashboard, kit.Navigation, kit.Localization);

        vm.Load(id, ServerDetailOrigin.Servers);
        Assert.Same(kit.Dashboard.VisibleServers.Single(card => card.Server.Id == id), vm.Card);
        Assert.Equal("prod-db-01", vm.Title);
        Assert.Equal("Servidores", vm.ParentText);

        // An edit rebuilds the list: the page follows the NEW card of the same server.
        var index = kit.Servers.Servers.FindIndex(server => server.Id == id);
        kit.Servers.Servers[index] = kit.Servers.Servers[index] with { Name = "prod-db-renamed" };
        await kit.Dashboard.LoadAsync();
        Assert.Same(kit.Dashboard.VisibleServers.Single(card => card.Server.Id == id), vm.Card);
        Assert.Equal("prod-db-renamed", vm.Title);
        Assert.Equal(0, kit.Navigation.ServersCount);

        // Hidden (or removed): back to the origin, exactly once.
        kit.Servers.Servers[index] = kit.Servers.Servers[index] with { IsHidden = true };
        await kit.Dashboard.LoadAsync();
        await kit.Dashboard.LoadAsync();
        Assert.Equal(1, kit.Navigation.ServersCount);
        Assert.Equal(0, kit.Navigation.DashboardCount);
    }

    [Fact]
    public async Task InterimPage_FromTheOverview_GoesBackToTheOverview_AndAnUnknownServerReturnsAtOnce()
    {
        var kit = Ui4TestKit.Create(FigmaFleet(), new ResWLocalizationService("en-US"));
        await kit.Dashboard.LoadAsync();

        using var unknown = new ServerDetailViewModel(kit.Dashboard, kit.Navigation, kit.Localization);
        unknown.Load(Guid.NewGuid(), ServerDetailOrigin.Overview);
        Assert.Equal(1, kit.Navigation.DashboardCount);
        Assert.Null(unknown.Card);

        using var vm = new ServerDetailViewModel(kit.Dashboard, kit.Navigation, kit.Localization);
        vm.Load(kit.Dashboard.VisibleServers[0].Server.Id, ServerDetailOrigin.Overview);
        Assert.Equal("Overview", vm.ParentText);
        vm.GoBackCommand.Execute(null);
        vm.GoBackCommand.Execute(null);
        Assert.Equal(2, kit.Navigation.DashboardCount);
    }

    private static void Set(object target, string field, object? value)
    {
        var info = typeof(DashboardViewModel).GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"No field {field}.");
        info.SetValue(target, value);
    }

    private sealed class EmptyDiscovery : ServerMonitor.Core.Interfaces.IServerDiscoveryService
    {
        public event EventHandler? DiscoveredChanged { add { } remove { } }

        public IReadOnlyList<ServerMonitor.Core.Discovery.DiscoveredService> GetDiscovered() => [];

        public Task IgnoreAsync(ServerMonitor.Core.Discovery.ServiceInstanceIdentity identity, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResetIgnoredAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
