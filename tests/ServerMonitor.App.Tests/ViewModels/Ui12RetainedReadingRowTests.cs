using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;
using ServerMonitor.Core.Monitoring;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.12 UI12A-STATE-03 (Prism, P2) and B12-06 (Beacon P2, Prism decision §3), over the real dashboard cards and the real
/// row view models (Servidores/Visão geral rows and the Compact row).
/// STATE-03: after a FAILED cycle the engine reports "Sem dados" (Unknown) and keeps the previous snapshot; the rows showed
/// those old values with severity colour and no cue. They now show "—" (as for Offline), and the Detail marks the reading stale.
/// B12-06: a row whose retained values are still shown (not hidden) and stale says "Última atualização há N min" - visible cue
/// and accessible name; a fresh row and an Offline row do not.
/// </summary>
public sealed class Ui12RetainedReadingRowTests
{
    [Fact]
    public async Task AFailedNoDataServer_ShowsDashes_NotItsRetainedValues_InEveryRow()
    {
        var fleet = new Ui4TestKit.Fleet().Add("db", ServerHealth.Healthy, cpu: 91, mem: 44, disk: 55);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();
        using var directory = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());
        using var compact = new CompactPresentationViewModel(kit.Dashboard, kit.Localization, MonitoringOptions.Default);
        var row = directory.Rows.Single();
        var compactRow = compact.Rows.Single();
        Assert.Equal("91%", row.CpuDisplay);

        kit.States.Set(kit.States.Get(fleet.IdOf("db")) with
        {
            Health = ServerHealth.Unknown,
            LastError = MetricsCollectionErrorCode.NoMetricsAvailable,
            LastAttemptAt = Ui4TestKit.Now,
        });

        Assert.True(row.Card.HasMetrics); // the snapshot is still held...
        Assert.Equal("—", row.CpuDisplay); // ...but never shown as current
        Assert.Equal("—", row.MemoryDisplay);
        Assert.Equal("—", row.DiskDisplay);
        Assert.False(row.HasCpuPercent);
        Assert.Equal("—", compactRow.CpuText);
        Assert.False(compactRow.IsCpuKnown);
        Assert.True(ServerStatusPresentation.ShowsRetainedReadingAsStale(
            row.Card.Health, row.Card.IsStale, row.Card.HasMetrics, row.Card.LastError is not null)); // the Detail's rule
    }

    [Fact]
    public async Task NoDataWithoutAFailure_HasNothingRetainedToHide()
    {
        var fleet = new Ui4TestKit.Fleet().Add("new", ServerHealth.Unknown, snapshot: false);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();
        using var directory = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());

        Assert.Equal("—", directory.Rows.Single().CpuDisplay);
        Assert.False(directory.Rows.Single().ShowsStaleCue);
    }

    [Fact]
    public async Task AStaleRowWithValues_ShowsTheAgeCue_AndNamesIt_AFreshOneDoesNot()
    {
        var fleet = new Ui4TestKit.Fleet().Add("old", ServerHealth.Healthy, cpu: 28, mem: 44, disk: 55);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();
        using var directory = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());
        var row = directory.Rows.Single();
        Assert.False(row.ShowsStaleCue);
        Assert.Null(row.StaleAgeDisplay);
        Assert.Equal("old, Saudável", row.ListAutomationName);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        kit.States.Set(kit.States.Get(fleet.IdOf("old")) with
        {
            IsStale = true,
            LastSuccessAt = Ui4TestKit.Now.AddMinutes(-7),
            LastAttemptAt = Ui4TestKit.Now,
        });

        Assert.True(row.ShowsStaleCue);
        Assert.Equal(row.Card.StaleAgeDisplay, row.StaleAgeDisplay);
        Assert.StartsWith("Última atualização há", row.StaleAgeDisplay, StringComparison.Ordinal);
        Assert.Equal("28%", row.CpuDisplay); // the values stay, qualified by the cue
        Assert.Equal($"old, Saudável, {row.StaleAgeDisplay}", row.ListAutomationName);
        Assert.EndsWith($", {row.StaleAgeDisplay}", row.RowAutomationName, StringComparison.Ordinal);
        Assert.Contains(nameof(ServerDirectoryRowViewModel.ShowsStaleCue), raised);
        Assert.Contains(nameof(ServerDirectoryRowViewModel.ListAutomationName), raised);
    }

    /// <summary>B12-06 wiring: the cue sits in the Overview row's trailing state and in the Wide and Mid status cells.</summary>
    [Theory]
    [InlineData("Views/DashboardPage.xaml", "OverviewServerRowTemplate")]
    [InlineData("Views/ServersPage.xaml", "ServerRowWideTemplate")]
    [InlineData("Views/ServersPage.xaml", "ServerRowMidTemplate")]
    public void TheRowTemplates_CarryTheStaleCue(string file, string template)
    {
        var xaml = Architecture.AppSourceTree.LoadXaml(file);
        var node = xaml.Descendants().Single(e => (string?)e.Attribute(Architecture.AppSourceTree.Xaml + "Key") == template);
        var cue = node.Descendants().Single(e => (string?)e.Attribute("Visibility") == "{x:Bind ShowsStaleCue, Mode=OneWay}");

        Assert.Contains(cue.Descendants(), e => (string?)e.Attribute("Data") == "{StaticResource SaIconClock01Data}");
        Assert.Contains(cue.Descendants(), e => (string?)e.Attribute("Text") == "{x:Bind StaleAgeDisplay, Mode=OneWay}");
        Assert.Contains(cue.Parent!.Elements(), e => e.Name.LocalName == "SaStatusIndicator"); // under the state
    }

    /// <summary>
    /// Prism r3 R3-01: in the Servers table the cue sits in a 150 px column; inside a horizontal StackPanel its text got
    /// infinite width, so it was clipped mid-age instead of trimmed or wrapped. It is a Grid (Auto, *) whose text wraps.
    /// </summary>
    [Theory]
    [InlineData("ServerRowWideTemplate")]
    [InlineData("ServerRowMidTemplate")]
    public void TheServersCue_Wraps_InsteadOfBeingClipped(string template)
    {
        var xaml = Architecture.AppSourceTree.LoadXaml("Views/ServersPage.xaml");
        var node = xaml.Descendants().Single(e => (string?)e.Attribute(Architecture.AppSourceTree.Xaml + "Key") == template);
        var cue = node.Descendants().Single(e => (string?)e.Attribute("Visibility") == "{x:Bind ShowsStaleCue, Mode=OneWay}");

        Assert.Equal("Grid", cue.Name.LocalName);
        var text = cue.Descendants().Single(e => e.Name.LocalName == "TextBlock");
        Assert.Equal("WrapWholeWords", (string?)text.Attribute("TextWrapping"));
        Assert.Equal("2", (string?)text.Attribute("MaxLines"));
        Assert.Equal("1", (string?)text.Attribute("Grid.Column"));
    }

    [Fact]
    public async Task AnOfflineRow_HasNoAgeCue_ItsValuesAreAlreadyDashes()
    {
        var fleet = new Ui4TestKit.Fleet().Add("down", ServerHealth.Healthy, cpu: 28, mem: 44, disk: 55);
        var kit = Ui4TestKit.Create(fleet, new ResWLocalizationService("pt-PT"));
        await kit.Dashboard.LoadAsync();
        using var directory = new ServersViewModel(kit.Dashboard, kit.Navigation, kit.Localization, TestClock.Fake());
        var row = directory.Rows.Single();

        kit.States.Set(kit.States.Get(fleet.IdOf("down")) with
        {
            Health = ServerHealth.Offline,
            IsStale = true,
            LastSuccessAt = Ui4TestKit.Now.AddMinutes(-7),
            LastAttemptAt = Ui4TestKit.Now,
        });

        Assert.False(row.ShowsStaleCue);
        Assert.Equal("—", row.CpuDisplay);
        Assert.DoesNotContain("Última atualização", row.RowAutomationName, StringComparison.Ordinal);
    }
}
