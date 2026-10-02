using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.History;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>UI.3 phase 1: History VM text, states and server selector (copy asserted against the real resw).</summary>
public sealed class HistoryViewModelUi3Tests
{
    // 29 Sep 2026 15:00 UTC — the Figma frame's "Últimas 24 horas · 28–29 set 2026".
    private static readonly DateTimeOffset End = new(2026, 9, 29, 15, 0, 0, TimeSpan.Zero);

    /// <summary>Query fake keyed by (server, range); queries complete immediately unless gated.</summary>
    private sealed class ScriptedHistoryQueryService : IServerHistoryQueryService
    {
        private readonly Dictionary<(Guid, HistoryTimeRange), TaskCompletionSource<ServerHistoryResult>> _gates = new();

        public bool IsAvailable { get; set; } = true;

        /// <summary>Ranges that hold data; every other range answers empty.</summary>
        public HashSet<HistoryTimeRange> RangesWithData { get; } = [];

        public double? Maximum { get; set; } = 78.2;

        public bool ThrowOn30DayProbe { get; set; }

        public List<(Guid ServerId, HistoryTimeRange Range)> Calls { get; } = [];

        public HashSet<Guid> GatedServers { get; } = [];

        public Task<ServerHistoryResult> GetHistoryAsync(Guid serverId, HistoryTimeRange range, CancellationToken cancellationToken)
        {
            Calls.Add((serverId, range));
            if (ThrowOn30DayProbe && range == HistoryTimeRange.Last30Days)
            {
                throw new InvalidOperationException("QA probe failure");
            }

            if (GatedServers.Contains(serverId))
            {
                // Inline continuations: Complete() runs the VM's continuation before returning.
                var tcs = new TaskCompletionSource<ServerHistoryResult>();
                _gates[(serverId, range)] = tcs;
                return tcs.Task;
            }

            return Task.FromResult(Result(serverId, range, RangesWithData.Contains(range), Maximum));
        }

        public void Complete(Guid serverId, HistoryTimeRange range, bool withData) =>
            _gates[(serverId, range)].SetResult(Result(serverId, range, withData, Maximum));
    }

    private static ServerHistoryResult Result(Guid serverId, HistoryTimeRange range, bool withData, double? maximum)
    {
        var start = End - range.ToDuration();
        var series = withData
            ? new HistorySeries
            {
                Points = [new HistoryChartPoint { TimestampUtc = start, Value = 10 }],
                MaxConnectGap = TimeSpan.FromMinutes(1),
                Latest = 10,
                Maximum = maximum
            }
            : HistorySeries.Empty;
        return new ServerHistoryResult
        {
            ServerId = serverId,
            Range = range,
            StartUtc = start,
            EndUtc = End,
            Cpu = series,
            Memory = series,
            Disk = series
        };
    }

    private sealed record Harness(
        HistoryViewModel Vm,
        ScriptedHistoryQueryService Query,
        FakeServerMetricsStore Metrics,
        FakeServerService Servers,
        ServerMonitoringStateStore States,
        FakeNavigationService Navigation);

    private static Harness New(string culture = "pt-PT", TimeZoneInfo? timeZone = null)
    {
        var query = new ScriptedHistoryQueryService();
        var metrics = new FakeServerMetricsStore();
        var servers = new FakeServerService();
        var states = new ServerMonitoringStateStore();
        var navigation = new FakeNavigationService();
        var time = new FakeTimeProvider(End);
        time.SetLocalTimeZone(timeZone ?? TimeZoneInfo.Utc);
        var vm = new HistoryViewModel(
            query,
            metrics,
            states,
            servers,
            navigation,
            new ResWLocalizationService(culture),
            NullLogger<HistoryViewModel>.Instance,
            time);
        return new Harness(vm, query, metrics, servers, states, navigation);
    }

    private static Server Server(string name, int order, bool hidden = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Host = "198.51.100.1",
        IsHidden = hidden,
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(order)
    };

    // --- Ranges / copy ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("pt-PT", "7 dias", "30 dias")]
    [InlineData("pt-BR", "7 dias", "30 dias")]
    [InlineData("en-US", "7 days", "30 days")]
    public void RangeCopy_UsesFullWords_ForDayRanges(string culture, string sevenDays, string thirtyDays)
    {
        var resources = ResWLocalizationService.Load(culture);

        Assert.Equal(sevenDays, resources["HistoryRangeLast7Days.Content"]);
        Assert.Equal(thirtyDays, resources["HistoryRangeLast30Days.Content"]);
        Assert.Equal("1 h", resources["HistoryRangeLastHour.Content"]);
    }

    [Theory]
    [InlineData(0, HistoryTimeRange.LastHour)]
    [InlineData(1, HistoryTimeRange.Last6Hours)]
    [InlineData(2, HistoryTimeRange.Last24Hours)]
    [InlineData(3, HistoryTimeRange.Last7Days)]
    [InlineData(4, HistoryTimeRange.Last30Days)]
    public void SelectedRangeIndex_QueriesTheMatchingRange(int index, HistoryTimeRange expected)
    {
        var h = New();
        h.Query.RangesWithData.Add(expected);
        h.Vm.SelectedRangeIndex = index == 0 ? 1 : 0;
        h.Query.Calls.Clear();

        h.Vm.SelectedRangeIndex = index;

        Assert.Equal(expected, h.Query.Calls[0].Range);
        Assert.True(h.Vm.ShowCharts);
    }

    // --- Empty-ever vs empty-period (D-UI3-10) -----------------------------------------------------------

    [Fact]
    public async Task EmptyRange_AndEmpty30Days_IsNeverRecorded()
    {
        var h = New();

        await h.Vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.Equal(HistoryEmptyKind.NeverRecorded, h.Vm.EmptyKind);
        Assert.True(h.Vm.ShowEmpty);
        Assert.True(h.Vm.ShowEmptyNeverRecorded);
        Assert.False(h.Vm.ShowEmptyPeriod);
        Assert.False(h.Vm.ShowViewLast30Days);
        Assert.Equal("O histórico começa aqui", h.Vm.EmptyTitle);
        Assert.Equal("Ver servidor", h.Vm.EmptyActionText);
        Assert.Equal(
            new[] { HistoryTimeRange.Last24Hours, HistoryTimeRange.Last30Days },
            h.Query.Calls.Select(call => call.Range));
    }

    [Fact]
    public async Task EmptyRange_WithDataIn30Days_IsEmptyPeriod_WithAction()
    {
        var h = New();
        h.Query.RangesWithData.Add(HistoryTimeRange.Last30Days);

        await h.Vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.Equal(HistoryEmptyKind.Period, h.Vm.EmptyKind);
        Assert.True(h.Vm.ShowEmptyPeriod);
        Assert.False(h.Vm.ShowEmptyNeverRecorded);
        Assert.True(h.Vm.ShowViewLast30Days);
        Assert.Equal("Sem leituras neste período", h.Vm.EmptyTitle);
        Assert.Contains("Experimenta", h.Vm.EmptyMessage, StringComparison.Ordinal);
        Assert.Equal("Ver últimos 30 dias", h.Vm.EmptyActionText);
    }

    [Fact]
    public void ViewLast30Days_SelectsThe30DayRange_AndShowsCharts()
    {
        var h = New();
        h.Query.RangesWithData.Add(HistoryTimeRange.Last30Days);
        h.Vm.Load(Guid.NewGuid(), "prod-web-01");
        Assert.True(h.Vm.ShowEmptyPeriod);

        h.Vm.ViewLast30DaysCommand.Execute(null);

        Assert.Equal(4, h.Vm.SelectedRangeIndex);
        Assert.True(h.Vm.ShowCharts);
        Assert.Equal(HistoryEmptyKind.None, h.Vm.EmptyKind);
        Assert.False(h.Vm.ShowViewLast30Days);
    }

    [Fact]
    public async Task Empty30DayRange_IsNeverRecorded_WithoutAnExtraProbe()
    {
        var h = New();

        await h.Vm.LoadRangeAsync(HistoryTimeRange.Last30Days);

        Assert.Equal(HistoryEmptyKind.NeverRecorded, h.Vm.EmptyKind);
        Assert.Single(h.Query.Calls);
    }

    [Fact]
    public async Task DataRange_NeverProbes30Days()
    {
        var h = New();
        h.Query.RangesWithData.Add(HistoryTimeRange.Last24Hours);

        await h.Vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.Equal(HistoryEmptyKind.None, h.Vm.EmptyKind);
        Assert.Single(h.Query.Calls);
    }

    [Fact]
    public async Task FailedProbe_FallsBackToPeriod_NeverClaimsNoHistory()
    {
        var h = New();
        h.Query.ThrowOn30DayProbe = true;

        await h.Vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.Equal(HistoryEmptyKind.Period, h.Vm.EmptyKind);
        Assert.False(h.Vm.IsUnavailable);
    }

    [Fact]
    public async Task Unavailable_And_Offline_StatesArePreserved()
    {
        var h = New();
        h.Query.IsAvailable = false;

        await h.Vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.True(h.Vm.ShowUnavailable);
        Assert.False(h.Vm.ShowEmptyNeverRecorded);
        Assert.False(h.Vm.ShowEmptyPeriod);
        Assert.Equal(HistoryEmptyKind.None, h.Vm.EmptyKind);
    }

    // --- Current value / peak -----------------------------------------------------------------------------

    [Fact]
    public async Task UnknownCurrentValue_IsDash_NeverZero()
    {
        var h = New();
        h.Query.RangesWithData.Add(HistoryTimeRange.Last24Hours);

        await h.Vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.Equal("—", h.Vm.CpuCurrentValue);
        Assert.False(h.Vm.HasCpuCurrent);
        Assert.Equal("—", h.Vm.MemoryCurrentValue);
        Assert.Equal("—", h.Vm.DiskCurrentValue);
        Assert.Equal("Atual", h.Vm.CurrentLabel);
    }

    [Fact]
    public async Task KnownCurrentValue_IsNumberWithoutUnit_PlusSeparateUnit()
    {
        var h = New();
        h.Metrics.InitialSnapshot = new ServerMetricsSnapshot
        {
            ServerId = Guid.NewGuid(),
            CollectedAt = End,
            CpuUsagePercent = 24.4,
            MemoryUsagePercent = 0
        };
        h.Query.RangesWithData.Add(HistoryTimeRange.Last24Hours);

        await h.Vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.Equal("24", h.Vm.CpuCurrentValue);
        Assert.True(h.Vm.HasCpuCurrent);
        Assert.Equal("%", h.Vm.PercentUnit);
        // A real 0 % reading is shown as 0 — only an unknown value is "—".
        Assert.Equal("0", h.Vm.MemoryCurrentValue);
        Assert.True(h.Vm.HasMemoryCurrent);
        Assert.Equal("—", h.Vm.DiskCurrentValue);
    }

    [Theory]
    [InlineData("pt-PT", "Pico no período 78%")]
    [InlineData("pt-BR", "Pico no período 78%")]
    [InlineData("en-US", "Peak in period 78%")]
    public async Task Peak_UsesRawSeriesMaximum(string culture, string expected)
    {
        var h = New(culture);
        h.Query.RangesWithData.Add(HistoryTimeRange.Last24Hours);

        await h.Vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.Equal(expected, h.Vm.CpuPeakDisplay);
        Assert.Equal(expected, h.Vm.DiskPeakDisplay);
    }

    [Fact]
    public async Task UnknownPeak_IsDash()
    {
        var h = New();
        h.Query.RangesWithData.Add(HistoryTimeRange.Last24Hours);
        h.Query.Maximum = null;

        await h.Vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.Equal("Pico no período —", h.Vm.CpuPeakDisplay);
    }

    // --- Footer / retention -----------------------------------------------------------------------------

    [Theory]
    [InlineData("pt-PT", "Últimas 24 horas · 28–29 set 2026")]
    [InlineData("pt-BR", "Últimas 24 horas · 28–29 set 2026")]
    [InlineData("en-US", "Last 24 hours · Sep 28–29, 2026")]
    public async Task PeriodFooter_IsFormattedInTheUiCulture(string culture, string expected)
    {
        var h = New(culture);
        h.Query.RangesWithData.Add(HistoryTimeRange.Last24Hours);

        await h.Vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        Assert.Equal(expected, h.Vm.PeriodFooter);
    }

    [Theory]
    [InlineData("pt-PT", HistoryTimeRange.LastHour, "Última hora · 29 set 2026")]
    [InlineData("en-US", HistoryTimeRange.LastHour, "Last hour · Sep 29, 2026")]
    [InlineData("pt-PT", HistoryTimeRange.Last30Days, "Últimos 30 dias · 30 ago – 29 set 2026")]
    [InlineData("pt-BR", HistoryTimeRange.Last30Days, "Últimos 30 dias · 30 ago – 29 set 2026")]
    [InlineData("en-US", HistoryTimeRange.Last30Days, "Last 30 days · Aug 30 – Sep 29, 2026")]
    public async Task PeriodFooter_CoversSameDayAndCrossMonth(string culture, HistoryTimeRange range, string expected)
    {
        var h = New(culture);
        h.Query.RangesWithData.Add(range);

        await h.Vm.LoadRangeAsync(range);

        Assert.Equal(expected, h.Vm.PeriodFooter);
    }

    [Theory]
    [InlineData("pt-PT", "28 dez 2025 – 3 jan 2026")]
    [InlineData("pt-BR", "28 dez 2025 – 3 jan 2026")]
    [InlineData("en-US", "Dec 28, 2025 – Jan 3, 2026")]
    public void FormatPeriod_CrossYear(string culture, string expected)
    {
        var resources = new ResWLocalizationService(culture);
        var text = HistoryPresentation.FormatPeriod(
            new DateTimeOffset(2025, 12, 28, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 3, 10, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc,
            HistoryPresentation.FormatCulture(culture),
            resources.GetString("HistoryPeriodSameDayFormat"),
            resources.GetString("HistoryPeriodSameMonthFormat"),
            resources.GetString("HistoryPeriodSameYearFormat"),
            resources.GetString("HistoryPeriodCrossYearFormat"));

        Assert.Equal(expected, text);
    }

    [Fact]
    public void RetentionCopy_IsPinnedToTheDefaultRetentionPeriod()
    {
        // D-UI3-11: the copy is fixed text. If the default retention ever changes, this fails so the copy
        // is updated instead of silently lying to the user.
        // Sentinel path only: constructing the options never touches the disk (RULES §3).
        var options = new HistoryStorageOptions { DatabasePath = Path.Combine(Path.GetTempPath(), "ui3-retention-sentinel.db") };
        var retentionDays = options.RetentionPeriod.TotalDays;
        Assert.Equal(30, retentionDays);

        foreach (var culture in ResWLocalizationService.Cultures)
        {
            var notice = ResWLocalizationService.Load(culture)["HistoryRetentionNotice"];
            Assert.Contains($"{retentionDays:0} ", notice, StringComparison.Ordinal);
        }

        Assert.Equal("Guardado neste dispositivo durante 30 dias.", New().Vm.RetentionNotice);
    }

    // --- Axes (D-UI3-4) -----------------------------------------------------------------------------------

    [Fact]
    public async Task XAxis_24Hours_FiveQuartileLabels_InLocalTime()
    {
        var h = New();
        h.Query.RangesWithData.Add(HistoryTimeRange.Last24Hours);

        await h.Vm.LoadRangeAsync(HistoryTimeRange.Last24Hours);

        // D-UI3-4 revised (Prism F6): round 6 h boundaries at their real position, not the quartiles of 15:00..15:00.
        Assert.Equal(new[] { "18:00", "00:00", "06:00", "12:00" }, h.Vm.XAxisLabels);
        Assert.Equal(new[] { "00:00", "12:00" }, h.Vm.XAxisLabelsCompact);
        Assert.Equal(new[] { 3 / 24d, 9 / 24d, 15 / 24d, 21 / 24d }, h.Vm.XAxisTicks.Select(t => t.Fraction));
        Assert.Equal(new[] { "100", "50", "0" }, h.Vm.YAxisLabels);
    }

    [Fact]
    public async Task XAxis_ConvertsToTheLocalTimeZone()
    {
        var lisbonSummer = TimeZoneInfo.CreateCustomTimeZone("QA+1", TimeSpan.FromHours(1), "QA+1", "QA+1");
        var h = New(timeZone: lisbonSummer);
        h.Query.RangesWithData.Add(HistoryTimeRange.LastHour);

        await h.Vm.LoadRangeAsync(HistoryTimeRange.LastHour);

        Assert.Equal(new[] { "15:00", "15:15", "15:30", "15:45", "16:00" }, h.Vm.XAxisLabels);
    }

    [Theory]
    [InlineData("pt-PT", new[] { "23 set", "25 set", "27 set", "29 set" })]
    [InlineData("pt-BR", new[] { "23 set", "25 set", "27 set", "29 set" })]
    [InlineData("en-US", new[] { "Sep 23", "Sep 25", "Sep 27", "Sep 29" })]
    public async Task XAxis_7Days_UsesDayLabelsInTheCulture(string culture, string[] expected)
    {
        var h = New(culture);
        h.Query.RangesWithData.Add(HistoryTimeRange.Last7Days);

        await h.Vm.LoadRangeAsync(HistoryTimeRange.Last7Days);

        Assert.Equal(expected, h.Vm.XAxisLabels);
    }

    [Fact]
    public void AxisTicks_AreEvenlySpaced_AndEndExactlyAtTheRangeEnd()
    {
        var start = End.AddHours(-6);

        var ticks = HistoryPresentation.AxisTicks(start, End, 5);

        Assert.Equal(5, ticks.Count);
        Assert.Equal(start, ticks[0]);
        Assert.Equal(start.AddMinutes(90), ticks[1]);
        Assert.Equal(End, ticks[4]);
        Assert.Throws<ArgumentOutOfRangeException>(() => HistoryPresentation.AxisTicks(start, End, 1));
    }

    [Fact]
    public void AxisLabels_EmptyForAnUnloadedRange()
    {
        var h = New();

        Assert.Empty(h.Vm.XAxisLabels);
        Assert.Empty(h.Vm.XAxisLabelsCompact);
        Assert.Equal(string.Empty, h.Vm.PeriodFooter);
    }

    [Fact]
    public void FormatCulture_DropsIcuMonthDots_ButKeepsTheLanguage()
    {
        var culture = HistoryPresentation.FormatCulture("pt-PT");

        Assert.Equal("pt-PT", culture.Name);
        Assert.Equal("28 set 2026", new DateTime(2026, 9, 28).ToString("d MMM yyyy", culture));
        Assert.Equal(CultureInfo.CurrentUICulture.Name, HistoryPresentation.FormatCulture(null).Name);
    }

    // --- Server selector ----------------------------------------------------------------------------------

    [Fact]
    public void Servers_ComeFromTheServerService_VisibleOnly_InDashboardOrder()
    {
        var h = New();
        var second = Server("db-02", order: 2);
        var first = Server("web-01", order: 1);
        var hidden = Server("hidden", order: 0, hidden: true);
        h.Servers.Servers.AddRange([second, hidden, first]);

        h.Vm.Load(second.Id, second.Name);

        Assert.Equal(new[] { "web-01", "db-02" }, h.Vm.Servers.Select(option => option.Name));
        Assert.Equal(second.Id, h.Vm.SelectedServer?.Id);
    }

    [Fact]
    public void SelectingAnotherServer_ReloadsThePageForThatServer()
    {
        var h = New();
        var a = Server("web-01", 1);
        var b = Server("db-02", 2);
        h.Servers.Servers.AddRange([a, b]);
        h.Query.RangesWithData.Add(HistoryTimeRange.Last24Hours);
        h.Vm.Load(a.Id, a.Name);
        h.Query.Calls.Clear();

        h.Vm.SelectedServer = h.Vm.Servers.Single(option => option.Id == b.Id);

        Assert.Equal("db-02", h.Vm.Title);
        Assert.Equal((b.Id, HistoryTimeRange.Last24Hours), h.Query.Calls[0]);
        Assert.Equal(b.Id, h.Vm.SelectedServer?.Id);
        Assert.True(h.Vm.ShowCharts);
    }

    [Fact]
    public void SwitchingServerMidQuery_DiscardsTheLateReplyForThePreviousServer()
    {
        var h = New();
        var a = Server("web-01", 1);
        var b = Server("db-02", 2);
        h.Servers.Servers.AddRange([a, b]);
        h.Query.GatedServers.Add(a.Id);
        h.Vm.Load(a.Id, a.Name);

        h.Vm.SelectedServer = h.Vm.Servers.Single(option => option.Id == b.Id);
        // b answers empty (→ NeverRecorded); the late, data-bearing reply for a must not win.
        h.Query.Complete(a.Id, HistoryTimeRange.Last24Hours, withData: true);

        Assert.Equal("db-02", h.Vm.Title);
        Assert.True(h.Vm.ShowEmptyNeverRecorded);
        Assert.Null(h.Vm.CpuSeries?.Maximum);
    }

    [Fact]
    public void ServerSubtitle_UsesOnlyKnownData()
    {
        var h = New();
        var known = Server("web-01", 1);
        var unknown = Server("db-02", 2);
        h.Servers.Servers.AddRange([known, unknown]);
        h.Metrics.InitialSnapshot = null;
        h.States.Set(new ServerMonitoringState
        {
            ServerId = known.Id,
            Health = ServerHealth.Healthy,
            LastSuccessAt = End
        });

        h.Vm.Load(known.Id, known.Name);

        Assert.Equal("Ligado", h.Vm.Servers[0].Subtitle);
        Assert.Equal(string.Empty, h.Vm.Servers[1].Subtitle);
        Assert.False(h.Vm.Servers[1].HasSubtitle);
    }

    [Fact]
    public void ServerSubtitle_DetectedOs_AndOffline()
    {
        var h = New();
        var server = Server("web-01", 1);
        h.Servers.Servers.Add(server);
        h.Metrics.InitialSnapshot = new ServerMetricsSnapshot
        {
            ServerId = server.Id,
            CollectedAt = End,
            OperatingSystemName = "Ubuntu",
            OperatingSystemVersion = "24.04"
        };
        h.States.Set(new ServerMonitoringState { ServerId = server.Id, Health = ServerHealth.Offline });

        h.Vm.Load(server.Id, server.Name);

        Assert.Equal("Ubuntu 24.04 · Sem ligação", h.Vm.Servers[0].Subtitle);
    }

    [Fact]
    public void ServerListFailure_KeepsTheCurrentServerSelectable()
    {
        var h = New();
        h.Servers.GetAllOverride = _ => throw new IOException("QA");
        var id = Guid.NewGuid();

        h.Vm.Load(id, "prod-web-01");

        var only = Assert.Single(h.Vm.Servers);
        Assert.Equal(id, only.Id);
        Assert.Equal(id, h.Vm.SelectedServer?.Id);
    }

    [Fact]
    public void SelectedServerSubtitle_ShowsLoadingCopyWhileLoading()
    {
        var h = New();
        var server = Server("web-01", 1);
        h.Servers.Servers.Add(server);
        h.Query.GatedServers.Add(server.Id);

        h.Vm.Load(server.Id, server.Name);

        Assert.True(h.Vm.IsLoading);
        Assert.Equal("A carregar histórico…", h.Vm.SelectedServerSubtitle);
    }

    [Fact]
    public void ViewServer_GoesToTheServersInterimPage_UntilUi5()
    {
        var h = New();

        h.Vm.ViewServerCommand.Execute(null);

        // UI.4 (Boss): the interim page is where the server's card lives now.
        Assert.Single(h.Navigation.ServerDetailReturns);
        Assert.Equal(0, h.Navigation.DashboardCount);
    }

    [Fact]
    public void Dispose_StopsALateServerListFromRepopulating()
    {
        var h = New();
        var gate = new TaskCompletionSource<IReadOnlyList<Server>>();
        h.Servers.GetAllOverride = _ => gate.Task;
        h.Vm.Load(Guid.NewGuid(), "prod-web-01");

        h.Vm.Dispose();
        gate.SetResult([Server("web-01", 1)]);

        Assert.Empty(h.Vm.Servers);
    }
}
