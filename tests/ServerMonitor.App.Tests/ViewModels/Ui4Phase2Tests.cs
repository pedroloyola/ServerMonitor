using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Monitoring;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.4 phase 2: the Prism r1 DERIVED decisions (.boss/tmp/ui4/prism/derived-r1.md) as view-model rules — bar segments
/// that always fit, the explicit "Mais N" footer, the hidden-servers note, "Sem leituras", the header context line, the
/// metric value severity (112:1468) and the §13/§14 copy — plus the Cortex r1 SHOULD-2 coalescing.
/// </summary>
public sealed class Ui4Phase2Tests
{
    private static HealthSummary Summary(int healthy, int warning = 0, int critical = 0, int offline = 0, int unknown = 0) =>
        HealthSummary.From(Enumerable.Repeat(ServerHealth.Healthy, healthy)
            .Concat(Enumerable.Repeat(ServerHealth.Warning, warning))
            .Concat(Enumerable.Repeat(ServerHealth.Critical, critical))
            .Concat(Enumerable.Repeat(ServerHealth.Offline, offline))
            .Concat(Enumerable.Repeat(ServerHealth.Unknown, unknown)));

    private static double BarLength(IReadOnlyList<HealthBarSegment> segments) =>
        segments.Sum(segment => segment.Width) + (OverviewPresentation.HealthBarGap * (segments.Count - 1));

    // ---- (a) bar segments -------------------------------------------------------------------------------------

    /// <summary>Prism r1 (a) bug: 496/1/1/1/1 overflowed to ~259.5 with a naive clamp. Reserve the minimum, share the rest.</summary>
    [Fact]
    public void AggregatedBar_AlwaysFillsExactly213_AndKeepsEveryPresentStateVisible()
    {
        var segments = OverviewPresentation.BarSegments(Summary(496, 1, 1, 1, 1));

        Assert.Equal(5, segments.Count);
        Assert.InRange(BarLength(segments), 212.5, 213.5);
        Assert.All(segments, segment => Assert.True(segment.Width >= OverviewPresentation.HealthSegmentMinWidth, $"{segment.Health}: {segment.Width}"));
        Assert.True(segments[0].Width > segments[1].Width);
        Assert.Equal([496, 1, 1, 1, 1], segments.Select(segment => segment.Count));
    }

    [Theory]
    [InlineData(13, 0, 0, 0, 0)]
    [InlineData(250, 250, 0, 0, 0)]
    [InlineData(100, 100, 100, 100, 100)]
    [InlineData(1, 0, 0, 0, 499)]
    public void AggregatedBar_FitsForAnyMix(int healthy, int warning, int critical, int offline, int unknown)
    {
        var segments = OverviewPresentation.BarSegments(Summary(healthy, warning, critical, offline, unknown));

        Assert.InRange(BarLength(segments), 212.5, 213.5);
        Assert.All(segments, segment => Assert.True(segment.Width >= OverviewPresentation.HealthSegmentMinWidth));
    }

    [Fact]
    public void DiscreteBar_IsTheFigma30Below7_AndEqualAndFittingUpTo12()
    {
        var six = OverviewPresentation.BarSegments(Summary(4, 1, 0, 1));
        Assert.All(six, segment => Assert.Equal(30, segment.Width));
        Assert.Equal(205, BarLength(six));

        var twelve = OverviewPresentation.BarSegments(Summary(12));
        Assert.Equal(12, twelve.Count);
        Assert.Single(twelve.Select(segment => segment.Width).Distinct());
        Assert.True(BarLength(twelve) <= 213.5);
        Assert.True(twelve[0].Width >= OverviewPresentation.HealthSegmentMinWidth);
    }

    [Fact]
    public async Task AggregatedSegments_CarryTheirCountAsTooltip_DiscreteOnesDoNot()
    {
        var many = Fleet(20, i => i == 0 ? ServerHealth.Warning : ServerHealth.Healthy);
        var kit = Ui4TestKit.Create(many, new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();
        Assert.Equal(["19 saudáveis", "1 em atenção"], kit.Dashboard.HealthBarSegments.Select(segment => segment.ToolTip));

        var few = Ui4TestKit.Create(Fleet(3, _ => ServerHealth.Healthy), new ResWLocalizationService("pt-PT"));
        await few.Dashboard.LoadAsync();
        Assert.All(few.Dashboard.HealthBarSegments, segment => Assert.Null(segment.ToolTip));
    }

    // ---- (b) footer --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TruncatedList_ShowsAnExplicitFooter_SearchFiltersEveryServer()
    {
        var kit = Ui4TestKit.Create(Fleet(21, _ => ServerHealth.Healthy), new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();

        Assert.True(kit.Dashboard.HasOverviewMore);
        Assert.Equal("Mais 13 servidores · Ver todos", kit.Dashboard.OverviewMoreDisplay);

        kit.Dashboard.OverviewSearchText = "s-20";
        Assert.Equal("s-20", Assert.Single(kit.Dashboard.OverviewServers).Name);
        Assert.False(kit.Dashboard.HasOverviewMore);

        kit.Dashboard.OverviewSearchText = "s-";
        Assert.Equal("Mais 13 servidores · Ver todos", kit.Dashboard.OverviewMoreDisplay);
        kit.Dashboard.OverviewSearchText = "s-1";
        Assert.Equal("Mais 3 servidores · Ver todos", kit.Dashboard.OverviewMoreDisplay); // s-1, s-10..s-19 = 11 → 8 + 3
    }

    // ---- (c) hidden-servers note --------------------------------------------------------------------------------

    [Fact]
    public async Task HiddenNote_FollowsTheFigma_AndTheFunctionalException()
    {
        var withRows = Ui4TestKit.Create(Fleet(2, _ => ServerHealth.Healthy));
        await withRows.Dashboard.LoadAsync();
        using (var directory = new ServersViewModel(withRows.Dashboard, withRows.Navigation, withRows.Localization, TestClock.Fake()))
        {
            Assert.True(directory.ShowHiddenServersNote); // rows, 0 hidden: always (Figma 112:1533)
            directory.SearchText = "nothing-matches";
            Assert.False(directory.ShowHiddenServersNote); // no-results state draws no note (§13 112:14194)
        }

        var onlyHidden = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("gone", ServerHealth.Healthy, 1, 1, 1, hidden: true));
        await onlyHidden.Dashboard.LoadAsync();
        using (var directory = new ServersViewModel(onlyHidden.Dashboard, onlyHidden.Navigation, onlyHidden.Localization, TestClock.Fake()))
        {
            Assert.True(directory.ShowEmptyState);
            Assert.True(directory.ShowHiddenServersNote); // empty + hidden: the note, or the user thinks they are lost
        }

        var empty = Ui4TestKit.Create(new Ui4TestKit.Fleet());
        await empty.Dashboard.LoadAsync();
        using var none = new ServersViewModel(empty.Dashboard, empty.Navigation, empty.Localization, TestClock.Fake());
        Assert.False(none.ShowHiddenServersNote); // §13 112:14007: no note
    }

    // ---- (e) Sem leituras / Sem problemas / copy ---------------------------------------------------------------

    [Fact]
    public async Task NoReadings_ReplacesNoProblems_WhenNothingCanBeJudged()
    {
        var allOffline = Ui4TestKit.Create(Fleet(6, _ => ServerHealth.Offline));
        await allOffline.Dashboard.LoadAsync();
        Assert.True(allOffline.Dashboard.ShowNoReadings);
        Assert.False(allOffline.Dashboard.ShowNoProblems);

        var oneReading = Ui4TestKit.Create(Fleet(6, i => i == 0 ? ServerHealth.Healthy : ServerHealth.Offline));
        await oneReading.Dashboard.LoadAsync();
        Assert.True(oneReading.Dashboard.ShowNoProblems);
        Assert.False(oneReading.Dashboard.ShowNoReadings);

        var unknown = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("new", ServerHealth.Unknown, snapshot: false));
        await unknown.Dashboard.LoadAsync();
        Assert.True(unknown.Dashboard.ShowNoReadings);
    }

    [Fact]
    public async Task HeaderContext_IsCollectingWhileLoading_EmptyWithoutReadings_ThenTheNewestAge()
    {
        var loading = Ui4TestKit.Create(new Ui4TestKit.Fleet(), new ResWLocalizationService("pt-PT"));
        var gate = new TaskCompletionSource<IReadOnlyList<Server>>();
        loading.Servers.GetAllOverride = _ => gate.Task;
        var load = loading.Dashboard.LoadAsync();
        Assert.Equal("A recolher dados…", loading.Dashboard.HeaderContextDisplay);
        gate.SetResult([]);
        await load;
        Assert.Null(loading.Dashboard.HeaderContextDisplay);
        Assert.False(loading.Dashboard.HasHeaderContext);

        // 5 offline with old readings + 1 fresh → the freshest (Prism r1 h).
        var mixed = Ui4TestKit.Create(new Ui4TestKit.Fleet()
            .Add("a", ServerHealth.Offline, 1, 1, 1, lastSuccess: Ui4TestKit.Now.AddHours(-3))
            .Add("b", ServerHealth.Healthy, 1, 1, 1, lastSuccess: Ui4TestKit.Now.AddMilliseconds(-200)), new ResWLocalizationService("pt-PT"));
        await mixed.Dashboard.LoadAsync();
        Assert.Equal("Atualizado agora mesmo", mixed.Dashboard.HeaderContextDisplay);

        var allOld = Ui4TestKit.Create(new Ui4TestKit.Fleet()
            .Add("a", ServerHealth.Offline, 1, 1, 1, lastSuccess: Ui4TestKit.Now.AddHours(-3))
            .Add("b", ServerHealth.Offline, 1, 1, 1, lastSuccess: Ui4TestKit.Now.AddHours(-5)), new ResWLocalizationService("pt-PT"));
        await allOld.Dashboard.LoadAsync();
        Assert.Equal("Atualizado há 3 h", allOld.Dashboard.HeaderContextDisplay);
    }

    [Fact]
    public async Task ServersCopy_FollowsFigma13And14()
    {
        var kit = Ui4TestKit.Create(Fleet(2, _ => ServerHealth.Healthy), new ResWLocalizationService("pt-PT"));
        var gate = new TaskCompletionSource<IReadOnlyList<Server>>();
        kit.Servers.GetAllOverride = _ => gate.Task;
        var load = kit.Dashboard.LoadAsync();
        using var directory = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());
        Assert.Equal("A carregar os teus servidores…", directory.HeaderContextDisplay);
        gate.SetResult(kit.Servers.Servers.ToList());
        await load;
        Assert.Equal("2 servidores · 2 saudáveis", directory.HeaderContextDisplay);

        directory.SearchText = "xyz";
        Assert.Equal("Nenhum servidor encontrado", directory.NoResultsTitle);
        Assert.Equal("Não há servidores com o nome ou endereço “xyz”. Experimenta outra pesquisa.", directory.NoResultsMessage);
    }

    // ---- (g) metric value severity -----------------------------------------------------------------------------

    [Theory]
    [InlineData(79.9, ServerHealth.Healthy)]
    [InlineData(80, ServerHealth.Warning)]
    [InlineData(88, ServerHealth.Warning)]
    [InlineData(90, ServerHealth.Critical)]
    public async Task DiskValue_IsColouredByTheEngineLimits(double disk, ServerHealth expected)
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("db", ServerHealth.Warning, 10, 10, disk));
        await kit.Dashboard.LoadAsync();

        Assert.Equal(expected, kit.Dashboard.OverviewServers.Single().DiskSeverity);
        Assert.Equal(ServerHealth.Healthy, kit.Dashboard.OverviewServers.Single().CpuSeverity);
    }

    [Fact]
    public async Task UnknownOrOfflineValues_AreNeverColoured()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet()
            .Add("off", ServerHealth.Offline, 99, 99, 99)
            .Add("partial", ServerHealth.Healthy, cpu: null, mem: 10, disk: 10));
        await kit.Dashboard.LoadAsync();

        Assert.All(kit.Dashboard.OverviewServers, row => Assert.Equal(
            [ServerHealth.Healthy, ServerHealth.Healthy, ServerHealth.Healthy],
            new[] { row.CpuSeverity, row.MemorySeverity, row.DiskSeverity }));
    }

    // ---- SHOULD-2 ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABurstOfEngineStates_IsRecomputedOnce_AndAChangeIsStillAnnounced()
    {
        var fleet = Fleet(50, _ => ServerHealth.Healthy);
        var kit = Ui4TestKit.Create(fleet);
        await kit.Dashboard.LoadAsync();
        var queued = new List<Action>();
        kit.Dashboard.OverviewScheduler = action =>
        {
            queued.Add(action);
            return true;
        };
        var summaryChanges = 0;
        kit.Dashboard.PropertyChanged += (_, e) => summaryChanges += e.PropertyName == nameof(DashboardViewModel.HealthSummary) ? 1 : 0;

        foreach (var entry in fleet.Entries)
        {
            kit.States.Set(kit.States.Get(entry.Server.Id) with { Health = ServerHealth.Offline });
        }

        Assert.Single(queued);
        Assert.Equal(0, summaryChanges); // nothing published until the queued pass runs
        queued[0]();
        Assert.Equal(1, summaryChanges);
        Assert.Equal(50, kit.Dashboard.HealthSummary.Offline);
        Assert.True(kit.Dashboard.ShowNoReadings);
    }

    private static Ui4TestKit.Fleet Fleet(int count, Func<int, ServerHealth> health)
    {
        var fleet = new Ui4TestKit.Fleet();
        for (var i = 0; i < count; i++)
        {
            var state = health(i);
            fleet.Add($"s-{i}", state, state == ServerHealth.Warning ? 85 : 10, 10, 10);
        }

        return fleet;
    }
}

/// <summary>The priority icon: exactly one of the six themed icons (UI.10 F19: one per metric) is visible for every
/// metric × severity.</summary>
public sealed class Ui4PriorityIconTests
{
    [Theory]
    [InlineData(PriorityMetric.Disk, ServerHealth.Warning, 0)]
    [InlineData(PriorityMetric.Disk, ServerHealth.Critical, 1)]
    [InlineData(PriorityMetric.Cpu, ServerHealth.Warning, 2)]
    [InlineData(PriorityMetric.Cpu, ServerHealth.Critical, 3)]
    [InlineData(PriorityMetric.Memory, ServerHealth.Warning, 4)]
    [InlineData(PriorityMetric.Memory, ServerHealth.Critical, 5)]
    public void ExactlyOneIconSlotIsVisible(PriorityMetric metric, ServerHealth severity, int visibleSlot)
    {
        for (var slot = 0; slot < 6; slot++)
        {
            Assert.Equal(
                slot == visibleSlot ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed,
                ServerMonitor.App.Converters.OverviewLayout.PriorityIcon(metric, severity, slot));
        }
    }
}
