using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.5 B2: the presentation the Figma pages bind (Server Detail 112:1785, Definições 112:7909, Dados e servidores
/// 112:8230), the Servidores one-shot notice (Boss B2 answer 1), the Data page's local toast, the two new metric
/// primitives' pure layout rules, and XAML contracts of the three pages. Synthetic data only; no wall clock.
/// </summary>
public sealed class Ui5B2Tests
{
    private static readonly DateTimeOffset Now = Ui4TestKit.Now;

    // ---- Boss B2 answer 4: metric text exactly as the rows (no % derived from bytes) ---------------------------------

    /// <summary>Atlas C1 finding 2: the number format is pinned (pt-PT) whatever the runner's culture (here en-US around it).</summary>
    [Fact]
    public async Task BytesWithoutAPercent_ShowADash_LikeTheRows_ButTheBytesStayAsTheCaption()
    {
        using var runner = new CultureScope("en-US");
        using var culture = new CultureScope("pt-PT");
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, cpu: 10, mem: null, disk: null);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        var id = fleet.IdOf("web");
        kit.Metrics.Snapshots[id] = kit.Metrics.Snapshots[id] with
        {
            MemoryUsedBytes = (long)(9.9 * 1024 * 1024 * 1024),
            MemoryTotalBytes = 16L * 1024 * 1024 * 1024,
            DiskUsedBytes = 240L * 1024 * 1024 * 1024,
            DiskTotalBytes = 500L * 1024 * 1024 * 1024
        };
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "web");
        using var row = new ServerDirectoryRowViewModel(detail.Card!, kit.Localization, _ => { });

        Assert.Equal(row.MemoryDisplay, detail.MemoryDisplay);
        Assert.Equal("—", detail.MemoryDisplay);
        Assert.Equal("—", detail.MemoryValueText);
        Assert.False(detail.HasMemoryPercent);
        Assert.Equal(-1, detail.MemoryLitCount);
        Assert.Equal("9,9 GB de 16 GB", detail.MemoryLegend);
        Assert.Equal("240 GB de 500 GB", detail.DiskLegend);
        Assert.Equal("—", detail.DiskDisplay);
    }

    [Fact]
    public async Task AKnownPercent_IsTheBigNumber_AndTheAccessibleNameReadsTheCard()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 24, 62, 48), new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "web");

        Assert.Equal("24", detail.CpuValueText);
        Assert.Equal("24%", detail.CpuDisplay);
        Assert.Equal("CPU: 24%", detail.CpuAccessibleName);
        Assert.Equal("Memória: 62%", detail.MemoryAccessibleName);
        Assert.Equal(17, detail.MemoryLitCount);
        Assert.Equal(7, detail.DiskLitCount);
        Assert.Null(detail.MemoryLegend); // no byte counts in the snapshot: no invented caption
    }

    [Theory]
    [InlineData(512L * 1024 * 1024, "512 MB")]
    [InlineData(1024L * 1024 * 1024, "1 GB")]
    [InlineData((long)(9.94 * 1024 * 1024 * 1024), "9,9 GB")]
    [InlineData(16L * 1024 * 1024 * 1024, "16 GB")]
    [InlineData(240L * 1024 * 1024 * 1024, "240 GB")]
    public void Bytes_UseTheCardsUnits(long bytes, string expected)
    {
        using var culture = new CultureScope("pt-PT");
        Assert.Equal(expected, MetricVisualPresentation.FormatBytes(bytes));
    }

    // ---- header, Ligação, info strip --------------------------------------------------------------------------------

    [Fact]
    public async Task TheHeaderAndConnectionRows_AreTheConfiguredServer()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3, host: "192.0.2.10");
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        var id = fleet.IdOf("web");
        kit.Metrics.Snapshots[id] = kit.Metrics.Snapshots[id] with { OperatingSystemName = "Ubuntu", OperatingSystemVersion = "24.04 LTS" };
        var index = kit.Servers.Servers.FindIndex(server => server.Id == id);
        kit.Servers.Servers[index] = kit.Servers.Servers[index] with { AuthenticationMethod = AuthenticationMethod.SshKey, Username = "monitor" };
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "web");

        Assert.Equal("Ubuntu 24.04 LTS   ·   192.0.2.10", detail.HeaderSubtitle); // Prism C1 N-3: Figma 112:1802 spacing
        Assert.Equal("192.0.2.10:22", detail.Address);
        Assert.Equal("monitor", detail.Username);
        Assert.Equal("Chave SSH", detail.AuthenticationDisplay);
        Assert.Equal("Ubuntu 24.04 LTS", detail.SystemDisplay);
    }

    [Theory]
    [InlineData(AuthenticationMethod.SshKey, "Chave SSH", "SSH key")]
    [InlineData(AuthenticationMethod.Password, "Palavra-passe", "Password")]
    [InlineData(AuthenticationMethod.NotConfigured, "Não configurada", "Not configured")]
    public async Task TheAuthenticationRow_NamesTheMethod_NeverASecret(AuthenticationMethod method, string ptPT, string enUS)
    {
        foreach (var (culture, expected) in new[] { ("pt-PT", ptPT), ("en-US", enUS) })
        {
            var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
            var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService(culture));
            kit.Servers.Servers[0] = kit.Servers.Servers[0] with { AuthenticationMethod = method };
            await kit.Dashboard.LoadAsync();
            using var detail = Open(kit, "web");

            Assert.Equal(expected, detail.AuthenticationDisplay);
        }
    }

    [Theory]
    [InlineData(1, "1 segundo")]
    [InlineData(30, "30 segundos")]
    [InlineData(60, "1 minuto")]
    [InlineData(300, "5 minutos")]
    [InlineData(90, "90 segundos")]
    public async Task TheInterval_IsTheServersRefreshInterval(int seconds, string expected)
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3), new ResWLocalizationService("pt-PT"));
        kit.Servers.Servers[0] = kit.Servers.Servers[0] with { RefreshIntervalSeconds = seconds };
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "web");

        Assert.Equal(expected, detail.IntervalDisplay);
    }

    [Theory]
    [InlineData(12, 8, 0, "12 dias, 8 horas")]
    [InlineData(1, 0, 0, "1 dia")]
    [InlineData(0, 3, 5, "3 horas, 5 minutos")]
    [InlineData(0, 1, 0, "1 hora")]
    [InlineData(0, 0, 7, "7 minutos")]
    [InlineData(0, 0, 0, "1 minuto")]
    public async Task Uptime_IsTheLongForm(int days, int hours, int minutes, string expected)
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        var id = fleet.IdOf("web");
        kit.Metrics.Snapshots[id] = kit.Metrics.Snapshots[id] with { Uptime = new TimeSpan(days, hours, minutes, 20) };
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "web");

        Assert.Equal(expected, detail.UptimeLongDisplay);
        Assert.Equal(expected, detail.UptimeDisplayOrDash);
    }

    [Fact]
    public async Task WithoutUptimeOrReading_TheStripSaysDash_NeverZero()
    {
        var kit = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("new", ServerHealth.Unknown, snapshot: false), new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();
        using var detail = Open(kit, "new");

        Assert.Equal("—", detail.UptimeDisplayOrDash);
        Assert.Equal("—", detail.LastUpdatedOrDash);
        Assert.Equal("A ligar…", detail.StatusLabel); // Figma 112:16093 while waiting for the first reading
        Assert.True(detail.IsFirstReading);
    }

    // ---- Boss B2 answer 1: the one-shot Servidores notice ------------------------------------------------------------

    [Fact]
    public async Task HidingHere_LeavesOneNotice_TheNextServersPageTakesItOnce()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3).Add("db", ServerHealth.Healthy, 1, 2, 3);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        kit.Servers.HideOverride = HideIn(kit);
        await kit.Dashboard.LoadAsync();
        var notice = new ServersReturnNotice();
        using var detail = Open(kit, "web", notice: notice);

        await ((AsyncRelayCommand)detail.HideCommand).ExecuteAsync();

        // Atlas C2 finding 1: never the system clock - and the recording fake proves the countdown went through it.
        var clock = new TimerRecordingTimeProvider();
        using var servers = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, new PresentationClock(clock), notice);
        Assert.Equal(1, clock.CreatedCount(TransientNoticeTimer.Duration));
        Assert.True(servers.IsNoticeOpen);
        Assert.Equal("Servidor ocultado", servers.NoticeTitle);
        Assert.Equal("Podes restaurar web nas Definições.", servers.NoticeMessage); // A-12 "tu"
        servers.DismissNoticeCommand.Execute(null);
        Assert.False(servers.IsNoticeOpen);

        using var again = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, new PresentationClock(clock), notice);
        Assert.False(again.IsNoticeOpen); // never re-shown
    }

    [Fact]
    public async Task RemovingHere_SaysRemoved()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        Ui4TestKit.Harness? kitRef = null;
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("en-US"),
            dialogs: new ConfirmingDialogs(), profiles: new RemovingProfiles(() => kitRef!));
        kitRef = kit;
        await kit.Dashboard.LoadAsync();
        var notice = new ServersReturnNotice();
        using var detail = Open(kit, "web", notice: notice);

        await ((AsyncRelayCommand)detail.RemoveCommand).ExecuteAsync();

        Assert.Equal(new ServersNotice(ServersNoticeKind.Removed, "web"), notice.Take());
    }

    [Fact]
    public async Task TheBreadcrumb_AnOutsideHide_OrAFailedHide_LeaveNoNotice()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3).Add("db", ServerHealth.Healthy, 1, 2, 3);
        var kit = Ui4TestKit.Create(fleet);
        await kit.Dashboard.LoadAsync();
        var notice = new ServersReturnNotice();

        using (var detail = Open(kit, "web", notice: notice))
        {
            detail.GoBackCommand.Execute(null);
        }

        using (var detail = Open(kit, "db", notice: notice))
        {
            kit.Servers.HideOverride = _ => Task.FromResult(false);
            await ((AsyncRelayCommand)detail.HideCommand).ExecuteAsync(); // fails: stays, no notice
            await HideIn(kit)(fleet.IdOf("db"));                          // then hidden elsewhere
        }

        Assert.Null(notice.Take());
    }

    [Fact]
    public async Task AFailedRemoveConfirmation_ThenTheBreadcrumb_IsNotReportedAsRemoved()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = Ui4TestKit.Create(fleet, dialogs: new ConfirmingDialogs { Confirm = false });
        await kit.Dashboard.LoadAsync();
        var notice = new ServersReturnNotice();
        using var detail = Open(kit, "web", ServerDetailOrigin.Overview, notice: notice);

        await ((AsyncRelayCommand)detail.RemoveCommand).ExecuteAsync(); // the user cancels the confirmation
        detail.GoBackCommand.Execute(null);

        Assert.Null(notice.Take());
        Assert.Equal(1, kit.Navigation.DashboardCount); // the breadcrumb goes to the origin
        Assert.Equal(0, kit.Navigation.ServersCount);
    }

    // ---- Dados e servidores: page-local toast, real version -----------------------------------------------------------

    [Fact]
    public async Task Successes_ShowTheLatestToast_ErrorsStayInline()
    {
        var settings = SettingsWorld.Create(new ResWLocalizationService("pt-PT"));
        settings.Servers.Servers.Add(new Server { Id = Guid.NewGuid(), Name = "old", Host = "old.local", IsHidden = true });
        var timersBefore = settings.Clock.CreatedCount(TransientNoticeTimer.Duration);
        settings.Servers.RestoreOverride = _ => Task.FromResult(true);
        await settings.ViewModel.LoadAsync();

        settings.ViewModel.HiddenServers[0].RestoreCommand.Execute(null);
        Assert.True(settings.ViewModel.IsToastOpen);
        Assert.Equal("Servidor restaurado", settings.ViewModel.ToastTitle);
        Assert.Equal("O servidor está novamente visível na lista.", settings.ViewModel.ToastMessage);

        settings.ViewModel.ResetIgnoredCommand.Execute(null);
        Assert.Equal("Dispositivos repostos", settings.ViewModel.ToastTitle);
        Assert.Equal(timersBefore + 2, settings.Clock.CreatedCount(TransientNoticeTimer.Duration)); // both on the fake

        settings.ViewModel.DismissToastCommand.Execute(null);
        Assert.False(settings.ViewModel.IsToastOpen);

        settings.Servers.RestoreOverride = _ => Task.FromResult(false);
        settings.Servers.Servers.Add(new Server { Id = Guid.NewGuid(), Name = "other", Host = "other.local", IsHidden = true });
        await settings.ViewModel.LoadAsync();
        settings.ViewModel.HiddenServers[^1].RestoreCommand.Execute(null);
        Assert.False(settings.ViewModel.IsToastOpen);
        Assert.True(settings.ViewModel.IsServerOperationErrorOpen);
    }

    [Fact]
    public void TheAboutCard_ShowsTheRealVersion()
    {
        var settings = SettingsWorld.Create(new ResWLocalizationService("pt-PT"));

        Assert.Equal("Versão " + settings.ViewModel.AppVersion, settings.ViewModel.AboutVersionText);
        Assert.False(string.IsNullOrWhiteSpace(settings.ViewModel.AppVersion));
    }

    // ---- primitives: pure layout ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1, 28, 0)]
    [InlineData(0, 28, 0)]
    [InlineData(17, 28, 17)]
    [InlineData(40, 28, 28)]
    [InlineData(5, 0, 0)]
    public void TheMeter_DrawsTheLitCount_UnknownDrawsNone(int lit, int count, int expected) =>
        Assert.Equal(expected, SaSegmentMeter.EffectiveLit(lit, count));

    /// <summary>
    /// Boss decision (fix round 2, revising the round-1 absolute scale): the bars are relative to a CEILING of 25 / 50 / 75
    /// / 100 % - the smallest step at or above the highest visible sample. DERIVED: a sample above 0 keeps the minimum
    /// visible height; a measured 0 draws no bar.
    /// </summary>
    [Theory]
    [InlineData(new double[0], 25)]
    [InlineData(new double[] { 24 }, 25)]
    [InlineData(new double[] { 25 }, 25)]
    [InlineData(new double[] { 25.1 }, 50)]
    [InlineData(new double[] { 3, 51, 10 }, 75)]
    [InlineData(new double[] { 76 }, 100)]
    [InlineData(new double[] { 150 }, 100)]
    [InlineData(new double[] { double.NaN, 10 }, 25)]
    public void ThePulseCeiling_IsTheSmallestStepHoldingTheMax(double[] samples, int ceiling) =>
        Assert.Equal(ceiling, MetricVisualPresentation.PulseCeiling(samples));

    [Fact]
    public void ThePulse_RightAlignsFewSamples_RampsOpacity_RelativeToTheCeiling()
    {
        var bars = SaPulseBars.Layout([12.5, 24, 0, 0.2], capacity: 30, trackHeight: 40, minimumHeight: 3, ceiling: 25);

        Assert.Equal(new[] { 26, 27, 28, 29 }, bars.Select(bar => bar.Slot));
        Assert.Equal(new[] { 20d, 38.4, 0d, 3d }, bars.Select(bar => bar.Height)); // 24 of 25 fills the track (Figma 112:1818)
        Assert.Equal(1d, bars[^1].Opacity);
        Assert.All(bars, bar => Assert.InRange(bar.Opacity, SaPulseBars.OldestOpacity, 1));
        Assert.Empty(SaPulseBars.Layout([], 30, 40, 3, 25)); // no samples: no bars

        var full = SaPulseBars.Layout(Enumerable.Range(1, 45).Select(i => (double)i).ToList(), 30, 40, 3, ceiling: 50);
        Assert.Equal(30, full.Count);
        Assert.Equal(SaPulseBars.OldestOpacity, full[0].Opacity);
        Assert.Equal(0, full[0].Slot);
        Assert.Equal(Math.Round(40 * 16 / 50d, 2), full[0].Height); // the 30 most recent (16…45), ceiling 50
    }

    [Theory]
    [InlineData(22, 25, 35.2)]
    [InlineData(24, 25, 38.4)]
    [InlineData(25, 25, 40)]
    [InlineData(60, 75, 32)]
    [InlineData(150, 100, 40)]
    [InlineData(0.2, 25, 3)]
    public void ThePulseHeight_IsTheShareOfTheCeiling(double sample, double ceiling, double height) =>
        Assert.Equal(height, Assert.Single(SaPulseBars.Layout([sample], 30, 40, 3, ceiling)).Height);

    // ---- Cortex B1 N-1 / N-7 / N-2 ------------------------------------------------------------------------------------

    /// <summary>N-1: a cancelled Remover leaves no intent behind once a later rebuild still lists the server.</summary>
    [Fact]
    public async Task ACancelledRemove_ThenAnUnrelatedRebuild_ThenAnOutsideHide_ReturnsToTheOrigin_WithoutNotice()
    {
        var fleet = new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3);
        var kit = Ui4TestKit.Create(fleet, dialogs: new ConfirmingDialogs { Confirm = false });
        await kit.Dashboard.LoadAsync();
        var notice = new ServersReturnNotice();
        using var detail = Open(kit, "web", ServerDetailOrigin.Overview, notice: notice);

        await ((AsyncRelayCommand)detail.RemoveCommand).ExecuteAsync(); // cancelled in its confirmation
        await kit.Dashboard.LoadAsync();                                // an unrelated rebuild: still listed
        await HideIn(kit)(fleet.IdOf("web"));                           // hidden elsewhere afterwards

        Assert.Null(notice.Take());
        Assert.Equal(1, kit.Navigation.DashboardCount); // the origin
        Assert.Equal(0, kit.Navigation.ServersCount);
    }

    /// <summary>N-7: an unexpected failure inside the load is logged and surfaced, never a silent success.</summary>
    [Fact]
    public async Task AnUnexpectedLoadFailure_IsSurfaced_NotSwallowed()
    {
        var servers = new FakeServerService();
        var viewModel = new SettingsViewModel(
            new Ui5SettingsTests.RecordingTheme(AppThemePreference.System),
            new FakeLocalizationService(),
            new FakeNavigationService(),
            servers,
            new Ui5SettingsTests.RecordingDiscovery(),
            new InertNotifications(),
            new FakeBackgroundMonitoringSettingsService(enabled: true),
            new BackgroundDegradationNotice(),
            new ThrowingMaintenance(),
            new AppVersionProvider(),
            NullLogger<SettingsViewModel>.Instance,
            new PresentationClock(new FakeTimeProvider(Now)));

        await viewModel.LoadAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(viewModel.IsServerOperationErrorOpen);
        await viewModel.LoadAsync().WaitAsync(TimeSpan.FromSeconds(30)); // and a later load still runs (not stuck)
    }

    [Theory]
    [InlineData("::1", 2222, "[::1]:2222")]
    [InlineData("::1", 22, "::1")]
    [InlineData("web.local", 2222, "web.local:2222")]
    [InlineData("[::1]", 2222, "[::1]:2222")]
    public void N2_TheRowAddress_UsesTheEndpointRule_WhenAPortIsShown(string host, int port, string expected) =>
        Assert.Equal(expected, OverviewPresentation.Address(host, port));

    /// <summary>
    /// A-12 (Boss): every pt-PT string the three UI.5 pages show uses "tu", never the formal forms. Atlas C1 finding 4: the
    /// markers are case-insensitive and the scan covers the x:Uid copy of the three pages AND the dynamic copy their view
    /// models put on them (toasts, notices, dialogs, accessible formats) - listed explicitly below. Prism C1 N-8 added
    /// "Reinicie". The M14.6 backup copy is outside A-12 (Prism N-14: backlog) and is not scanned.
    /// </summary>
    [Fact]
    public void ThePtPtCopyOfTheUi5Pages_UsesTu()
    {
        var offenders = TonedOffenders(Ui5PtPtKeys());

        Assert.Empty(offenders);
    }

    /// <summary>The tone scan's own counterproof: a formal string in either case, static or dynamic, is caught.</summary>
    [Theory]
    [InlineData("Tente novamente mais tarde.")]
    [InlineData("tente novamente mais tarde.")]
    [InlineData("VOCÊ pode restaurar o servidor.")]
    [InlineData("Reinicie a aplicação.")]
    [InlineData("Guarde os seus servidores.")]
    public void TheToneScan_CatchesFormalCopy_InAnyCase(string formal) =>
        Assert.True(FormalPtPt.IsMatch(formal), formal);

    private static readonly System.Text.RegularExpressions.Regex FormalPtPt = new(
        @"\b(você|tente|guarde|escolha|utilize|receba|reponha|abra|selecione|introduza|reinicie|confirme|verifique|aguarde)\b|\ba sua\b|\bos seus\b|\bque pode\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // The dynamic copy the three pages show (resource-key prefixes read by their view models / dialogs).
    private static readonly string[] Ui5DynamicPrefixes =
    [
        "ServerDetail", "ServersNotice", "SettingsServerRestored", "SettingsResetIgnored", "SettingsHistory", "SettingsToast",
        "SettingsAboutVersion", "RemoveServerConfirm", "HistoryClearConfirm", "HistoryResetConfirm", "DestructiveConfirm",
        "ServerOperationError", "ServerStatus", "ServerMetric", "HiddenServerRestore", "SettingsRestartNotice"
    ];

    private static Dictionary<string, string> Ui5PtPtKeys()
    {
        var resources = ResWLocalizationService.Load("pt-PT");
        var uids = new[] { "Views/ServerDetailPage.xaml", "Views/SettingsPage.xaml", "Views/SettingsDataPage.xaml" }
            .SelectMany(file => AppSourceTree.LoadXaml(file).Descendants())
            .Select(e => (string?)e.Attribute(AppSourceTree.Xaml + "Uid")).OfType<string>().ToHashSet();
        return resources
            .Where(entry => (uids.Contains(entry.Key.Split('.')[0]) || Ui5DynamicPrefixes.Any(prefix => entry.Key.StartsWith(prefix, StringComparison.Ordinal)))
                && !entry.Key.StartsWith("Backup", StringComparison.Ordinal)) // M14.6: Prism N-14 backlog, outside A-12
            .ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    private static List<string> TonedOffenders(Dictionary<string, string> keys) =>
        keys.Where(entry => FormalPtPt.IsMatch(entry.Value)).Select(entry => $"{entry.Key} = {entry.Value}").ToList();

    private sealed class ThrowingMaintenance : IHistoryMaintenanceService
    {
        public bool IsAvailable => throw new InvalidOperationException("synthetic");

        public Task<HistoryClearOutcome> ClearHistoryWithConfirmationAsync() => throw new NotSupportedException();

        public Task<HistoryResetOutcome> ResetHistoryWithConfirmationAsync() => throw new NotSupportedException();
    }

    // ---- XAML contracts of the three pages ----------------------------------------------------------------------------

    public static TheoryData<string> Ui5Pages => new() { "Views/ServerDetailPage.xaml", "Views/SettingsPage.xaml", "Views/SettingsDataPage.xaml" };

    [Theory]
    [MemberData(nameof(Ui5Pages))]
    public void EveryXUid_ResolvesInEveryCulture_AndPagesUseOnlyTokens(string file)
    {
        var resources = ResWLocalizationService.Cultures.ToDictionary(c => c, c => ResWLocalizationService.Load(c));
        var elements = AppSourceTree.LoadXaml(file).Descendants().ToList();
        var failures = new List<string>();
        foreach (var uid in elements.Select(e => (string?)e.Attribute(AppSourceTree.Xaml + "Uid")).OfType<string>().Distinct())
        {
            foreach (var culture in ResWLocalizationService.Cultures)
            {
                if (!resources[culture].Keys.Any(k => k.StartsWith(uid + ".", StringComparison.Ordinal)))
                {
                    failures.Add($"{culture}: x:Uid '{uid}' has no key");
                }
            }
        }

        failures.AddRange(elements.SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName is "FontSize" or "CornerRadius" or "Padding" && !a.Value.StartsWith('{'))
            .Select(a => $"literal {a.Parent!.Name.LocalName}.{a.Name.LocalName}=\"{a.Value}\""));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [MemberData(nameof(Ui5Pages))]
    public void EachPage_HasOneH1_AndNoHorizontalScroll(string file)
    {
        var elements = AppSourceTree.LoadXaml(file).Descendants().ToList();

        Assert.Single(elements, e => (string?)e.Attribute(XName.Get("AutomationProperties.HeadingLevel")) == "Level1");
        Assert.All(elements.Where(e => e.Name.LocalName == "ScrollViewer"),
            sv => Assert.Equal("Disabled", (string?)sv.Attribute("HorizontalScrollBarVisibility")));
    }

    [Fact]
    public void TheDetail_DrawsTheFigmaVisuals_AndThePulseOnlyFromRealSamples()
    {
        var elements = AppSourceTree.LoadXaml("Views/ServerDetailPage.xaml").Descendants().ToList();
        var meters = elements.Where(e => e.Name.LocalName == "SaSegmentMeter").ToList();

        Assert.Equal(new[] { "28", "14" }, meters.Select(m => (string?)m.Attribute("SegmentCount")));
        Assert.Equal(new[] { "{Binding MemoryLitCount}", "{Binding DiskLitCount}" }, meters.Select(m => (string?)m.Attribute("LitCount")));
        Assert.Equal("{Binding CpuPulseSamples}", (string?)Assert.Single(elements, e => e.Name.LocalName == "SaPulseBars").Attribute("Samples"));
        Assert.Contains(elements, e => (string?)e.Attribute("Style") == "{StaticResource SaIconTileStyle}");
        Assert.Equal(2, elements.Count(e => (string?)e.Attribute("Style") == "{StaticResource SaCardTitleTextStyle}"));
        // H-UI5-1: Ocultar / Remover live in the "…" menu next to Editar, bound to the Detail's commands.
        Assert.Equal(new[] { "{Binding HideCommand}", "{Binding RemoveCommand}" },
            elements.Where(e => e.Name.LocalName == "MenuFlyoutItem").Select(e => (string?)e.Attribute("Command")));
        Assert.DoesNotContain(elements, e => e.Name.LocalName == "ServerFullCard");
    }

    /// <summary>H-UI5-4 / A-11: Backup/Restore is a card in "Dados e servidores" between History and About.</summary>
    [Fact]
    public void TheBackupCard_SitsBetweenHistoryAndAbout()
    {
        var uids = AppSourceTree.LoadXaml("Views/SettingsDataPage.xaml").Descendants()
            .Select(e => (string?)e.Attribute(AppSourceTree.Xaml + "Uid")).OfType<string>().ToList();

        var history = uids.IndexOf("SettingsHistoryTitle");
        var backup = uids.IndexOf("BackupSectionTitle");
        var about = uids.IndexOf("SettingsAboutTitle");
        Assert.True(history >= 0 && history < backup && backup < about, $"history {history}, backup {backup}, about {about}");
    }

    /// <summary>A-6 token guard: the card title is 17 / 24 Semibold, measured on two pages.</summary>
    [Fact]
    public void TheCardTitleToken_Is17Over24Semibold()
    {
        var typography = AppSourceTree.LoadXaml("Styles/Tokens/Typography.xaml").Descendants().ToList();
        string Value(string key) => typography.Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == key).Value;

        Assert.Equal("17", Value("SaFontSizeCardTitle"));
        Assert.Equal("24", Value("SaLineHeightCardTitle"));
        var style = typography.Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Key") == "SaCardTitleTextStyle");
        Assert.Contains(style.Elements(), setter => (string?)setter.Attribute("Property") == "FontWeight" && (string?)setter.Attribute("Value") == "SemiBold");
    }

    [Fact]
    public void TheThemeSelector_IsThreeRadioButtons_BoundToTheThemeIndex()
    {
        var radios = AppSourceTree.LoadXaml("Views/SettingsPage.xaml").Descendants().Where(e => e.Name.LocalName == "RadioButton").ToList();

        Assert.Equal(3, radios.Count);
        Assert.All(radios, r => Assert.Contains("SelectedThemeIndex", (string?)r.Attribute("IsChecked"), StringComparison.Ordinal));
        Assert.Equal(new[] { "ConverterParameter=0}", "ConverterParameter=1}", "ConverterParameter=2}" },
            radios.Select(r => ((string)r.Attribute("IsChecked")!).Split(", ")[^1]));
    }

    [Fact]
    public void TheToasts_AreAnnouncedPolitely()
    {
        foreach (var file in new[] { "Views/ServersPage.xaml", "Views/SettingsDataPage.xaml" })
        {
            var toast = Assert.Single(AppSourceTree.LoadXaml(file).Descendants(), e => e.Name.LocalName == "SaToast");
            Assert.Equal("Polite", (string?)toast.Attribute(XName.Get("AutomationProperties.LiveSetting")));
            Assert.NotNull(toast.Attribute("CloseButtonAutomationName"));
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static ServerDetailViewModel Open(
        Ui4TestKit.Harness kit, string name, ServerDetailOrigin origin = ServerDetailOrigin.Overview, ServersReturnNotice? notice = null)
    {
        var detail = new ServerDetailViewModel(kit.Dashboard, kit.Navigation, kit.Localization, history: null, returnFocus: null,
            monitoringOptions: null, clock: new PresentationClock(new FakeTimeProvider(Now)), serversNotice: notice);
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

    /// <summary>Atlas C1 finding 2: pins BOTH the UI culture and the formatting culture, then restores them.</summary>
    internal sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previousUi = System.Globalization.CultureInfo.CurrentUICulture;
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string culture)
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
        }

        public void Dispose()
        {
            System.Globalization.CultureInfo.CurrentUICulture = _previousUi;
            System.Globalization.CultureInfo.CurrentCulture = _previous;
        }
    }

    private sealed class ConfirmingDialogs : IServerDialogService
    {
        public bool Confirm { get; init; } = true;

        public Task<ServerEditorResult?> ShowEditorAsync(Server? server) => Task.FromResult<ServerEditorResult?>(null);

        public Task<ServerEditorResult?> ShowEditorForDiscoveryAsync(ServerDiscoveryPrefill prefill) => Task.FromResult<ServerEditorResult?>(null);

        public Task<ServerEditorResult?> ShowEditorForSshImportAsync() => Task.FromResult<ServerEditorResult?>(null);

        public Task<bool> ConfirmRemoveAsync(Server server) => Task.FromResult(Confirm);
    }

    private sealed class RemovingProfiles(Func<Ui4TestKit.Harness> kit) : Core.Interfaces.IServerProfileService
    {
        public Task<Core.Domain.ServerOperationResult> AddAsync(ServerProfileInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Core.Domain.ServerOperationResult> UpdateAsync(Server current, ServerProfileInput input, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(Server server, CancellationToken cancellationToken = default)
        {
            var harness = kit();
            harness.Servers.Servers.RemoveAll(candidate => candidate.Id == server.Id);
            harness.Servers.RaiseChanged();
            return Task.FromResult(true);
        }
    }

    private sealed class SettingsWorld
    {
        public required SettingsViewModel ViewModel { get; init; }

        public required FakeServerService Servers { get; init; }

        /// <summary>Atlas C2 finding 1: the toast's countdown runs on this fake, never on the system clock.</summary>
        public required TimerRecordingTimeProvider Clock { get; init; }

        public static SettingsWorld Create(ILocalizationService localization)
        {
            var servers = new FakeServerService();
            var clock = new TimerRecordingTimeProvider();
            var viewModel = new SettingsViewModel(
                new Ui5SettingsTests.RecordingTheme(AppThemePreference.System),
                localization,
                new FakeNavigationService(),
                servers,
                new Ui5SettingsTests.RecordingDiscovery(),
                new InertNotifications(),
                new FakeBackgroundMonitoringSettingsService(enabled: true),
                new BackgroundDegradationNotice(),
                new NullHistoryMaintenanceService(),
                new AppVersionProvider(),
                NullLogger<SettingsViewModel>.Instance,
                new PresentationClock(clock));
            return new SettingsWorld { ViewModel = viewModel, Servers = servers, Clock = clock };
        }
    }

    private sealed class InertNotifications : INotificationSettingsService
    {
        public event EventHandler? NotificationsEnabledChanged { add { } remove { } }

        public bool NotificationsEnabled => true;

        public void SetNotificationsEnabled(bool enabled)
        {
        }
    }
}
