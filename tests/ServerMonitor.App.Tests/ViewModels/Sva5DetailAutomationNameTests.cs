using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Models;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// SVA-5 (B12F-02): the Servidores row button is named by <see cref="ServerDirectoryRowViewModel.DetailAutomationName"/>,
/// which Narrator reads first; the stale age reached only the help text, and the OneTime binding never refreshed the name.
/// The name now carries ", Última atualização há N min" while the row is stale (same rule as ListAutomationName), follows
/// the transitions through a OneWay binding, and the Mid row's visible tooltip keeps today's text.
/// </summary>
public sealed class Sva5DetailAutomationNameTests
{
    [Fact]
    public async Task AFreshRow_IsNamedByItsActionOnly()
    {
        var (_, _, row, directory) = await RowAsync();
        using var _ = directory;

        Assert.Null(row.StaleAgeDisplay);
        Assert.Equal("Ver detalhe de old", row.DetailAutomationName);
        Assert.Equal("Ver detalhe de old", row.DetailToolTip);
    }

    [Fact]
    public async Task FreshToStale_AppendsTheAge_AndRaisesTheName()
    {
        var (kit, fleet, row, directory) = await RowAsync();
        using var _ = directory;
        var raised = Record(row);

        kit.States.Set(Stale(kit, fleet, minutes: 7));

        Assert.Equal("Ver detalhe de old, Última atualização há 7 min", row.DetailAutomationName);
        Assert.Equal($"Ver detalhe de old, {row.StaleAgeDisplay}", row.DetailAutomationName);
        Assert.Equal("Ver detalhe de old", row.DetailToolTip); // the visible tooltip never carries the age
        Assert.Contains(nameof(ServerDirectoryRowViewModel.DetailAutomationName), raised);
    }

    [Fact]
    public async Task StaleToFresh_DropsTheAge_AndRaisesTheName()
    {
        var (kit, fleet, row, directory) = await RowAsync();
        using var _ = directory;
        kit.States.Set(Stale(kit, fleet, minutes: 7));
        var raised = Record(row);

        kit.States.Set(kit.States.Get(fleet.IdOf("old")) with
        {
            IsStale = false,
            LastSuccessAt = Ui4TestKit.Now,
            LastAttemptAt = Ui4TestKit.Now,
        });

        Assert.Equal("Ver detalhe de old", row.DetailAutomationName);
        Assert.Contains(nameof(ServerDirectoryRowViewModel.DetailAutomationName), raised);
    }

    [Fact]
    public async Task ANewAge_RaisesTheName_WithTheNewAge()
    {
        var (kit, fleet, row, directory) = await RowAsync();
        using var _ = directory;
        kit.States.Set(Stale(kit, fleet, minutes: 7));
        var raised = Record(row);

        kit.States.Set(Stale(kit, fleet, minutes: 12));

        Assert.Equal("Ver detalhe de old, Última atualização há 12 min", row.DetailAutomationName);
        Assert.Contains(nameof(ServerDirectoryRowViewModel.DetailAutomationName), raised);
    }

    [Theory]
    [InlineData(ServerHealth.Offline, null)]
    [InlineData(ServerHealth.Unknown, MetricsCollectionErrorCode.NoMetricsAvailable)] // a failed "Sem dados" (STATE-03)
    public async Task AnOfflineOrFailedNoDataRow_NeverGetsAnAge(ServerHealth health, MetricsCollectionErrorCode? error)
    {
        var (kit, fleet, row, directory) = await RowAsync();
        using var _ = directory;

        kit.States.Set(Stale(kit, fleet, minutes: 7) with { Health = health, LastError = error });

        Assert.Null(row.StaleAgeDisplay);
        Assert.Equal("Ver detalhe de old", row.DetailAutomationName);
    }

    [Fact]
    public async Task AnIrrelevantChange_DoesNotRaiseTheName()
    {
        var (kit, fleet, row, directory) = await RowAsync();
        using var _ = directory;
        kit.States.Set(Stale(kit, fleet, minutes: 7));
        var raised = Record(row);

        row.Card.UpdateConnectionState(new SshConnectionResult { State = ServerConnectionState.Connected });

        Assert.DoesNotContain(nameof(ServerDirectoryRowViewModel.DetailAutomationName), raised);
        Assert.DoesNotContain(string.Empty, raised);
    }

    [Theory]
    [InlineData("pt-PT", "Ver detalhe de old", "Ver detalhe de old, Última atualização há 7 min")]
    [InlineData("pt-BR", "Ver detalhes de old", "Ver detalhes de old, Última atualização há 7 min")]
    [InlineData("en-US", "View details for old", "View details for old, Last updated 7 min ago")]
    public async Task TheName_ComposesInEveryCulture(string culture, string fresh, string stale)
    {
        var (kit, fleet, row, directory) = await RowAsync(culture);
        using var _ = directory;
        Assert.Equal(fresh, row.DetailAutomationName);

        kit.States.Set(Stale(kit, fleet, minutes: 7));

        Assert.Equal(stale, row.DetailAutomationName);
        Assert.Equal(fresh, row.DetailToolTip);
    }

    /// <summary>The three row templates name the button OneWay; the Mid row's tooltip is the age-free text.</summary>
    [Fact]
    public void TheXaml_BindsTheNameOneWay_AndTheTooltipWithoutTheAge()
    {
        var xaml = AppSourceTree.LoadXaml("Views/ServersPage.xaml");
        var names = xaml.Descendants()
            .Select(e => (string?)e.Attribute("AutomationProperties.Name"))
            .Where(v => v is not null && v.Contains("DetailAutomationName", StringComparison.Ordinal))
            .ToList();
        var toolTips = xaml.Descendants().Select(e => (string?)e.Attribute("ToolTipService.ToolTip")).Where(v => v is not null).ToList();

        Assert.Equal(3, names.Count);
        Assert.All(names, v => Assert.Equal("{x:Bind DetailAutomationName, Mode=OneWay}", v));
        Assert.DoesNotContain(toolTips, v => v!.Contains("DetailAutomationName", StringComparison.Ordinal));
        Assert.Single(toolTips, v => v == "{x:Bind DetailToolTip}");
    }

    /// <summary>
    /// UI.8 lesson, the compiled x:Bind the build generated (what runs per realized row): each template listens for
    /// DetailAutomationName and re-sets AutomationProperties.Name on DATA_CHANGED; the tooltip setter stays OneTime.
    /// </summary>
    [Fact]
    public void TheCompiledBinding_RefreshesTheName_AndNotTheTooltip()
    {
        var generated = File.ReadAllText(GeneratedServersPage());

        Assert.Equal(3, Count(generated, "case \"DetailAutomationName\":"));
        var nameSetters = Bodies(generated, "private void Update_DetailAutomationName(global::System.String obj, int phase)");
        Assert.Equal(3, nameSetters.Count);
        Assert.All(nameSetters, body =>
        {
            Assert.Contains("DATA_CHANGED", body, StringComparison.Ordinal);
            Assert.Contains("Set_Microsoft_UI_Xaml_Automation_AutomationProperties_Name", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ToolTip", body, StringComparison.Ordinal);
        });
        var toolTip = Assert.Single(Bodies(generated, "private void Update_DetailToolTip(global::System.String obj, int phase)"));
        Assert.Contains("Set_Microsoft_UI_Xaml_Controls_ToolTipService_ToolTip", toolTip, StringComparison.Ordinal);
        Assert.DoesNotContain("DATA_CHANGED", toolTip, StringComparison.Ordinal);
    }

    // ---- helpers ----

#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    private static async Task<(Ui4TestKit.Harness Kit, Ui4TestKit.Fleet Fleet, ServerDirectoryRowViewModel Row, ServersViewModel Directory)> RowAsync(
        string culture = "pt-PT")
    {
        var fleet = new Ui4TestKit.Fleet().Add("old", ServerHealth.Healthy, cpu: 28, mem: 44, disk: 55);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService(culture));
        await kit.Dashboard.LoadAsync();
        var directory = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());
        return (kit, fleet, directory.Rows.Single(), directory);
    }

    private static ServerMonitoringState Stale(Ui4TestKit.Harness kit, Ui4TestKit.Fleet fleet, int minutes) =>
        kit.States.Get(fleet.IdOf("old")) with
        {
            IsStale = true,
            LastSuccessAt = Ui4TestKit.Now.AddMinutes(-minutes),
            LastAttemptAt = Ui4TestKit.Now,
        };

    private static List<string?> Record(ServerDirectoryRowViewModel row)
    {
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }

    private static string GeneratedServersPage()
    {
        var path = Path.Combine(AppSourceTree.AppRoot, "obj", "x64", Configuration, "net10.0-windows10.0.19041.0", "win-x64", "Views", "ServersPage.g.cs");
        Assert.True(File.Exists(path), $"the XAML compiler output is missing: {path}");
        return path;
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // Every method body that starts at the signature (brace-matched).
    private static List<string> Bodies(string code, string signature)
    {
        var bodies = new List<string>();
        for (var start = code.IndexOf(signature, StringComparison.Ordinal); start >= 0; start = code.IndexOf(signature, start + 1, StringComparison.Ordinal))
        {
            var open = code.IndexOf('{', start);
            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                depth += code[i] switch { '{' => 1, '}' => -1, _ => 0 };
                if (depth == 0)
                {
                    bodies.Add(code[open..(i + 1)]);
                    break;
                }
            }
        }

        return bodies;
    }
}
