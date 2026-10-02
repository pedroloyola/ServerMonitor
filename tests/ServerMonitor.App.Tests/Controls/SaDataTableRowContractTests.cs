using System.Reflection;
using Microsoft.UI.Xaml.Automation.Peers;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.Tests.Architecture;
using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Workloads;

namespace ServerMonitor.App.Tests.Controls;

/// <summary>
/// UI.3 difference 14 (Boss): workload rows are reachable and walkable by keyboard but NOT interactive. Read without a
/// XAML runtime; the runtime check is the UIA pass (Tab into the list, arrows move row focus, ListItem, no pattern).
/// </summary>
public sealed class SaDataTableRowContractTests
{
    [Fact]
    public void Row_IsAListItem_WithNoControlPattern()
    {
        Assert.Equal(AutomationControlType.ListItem, SaDataTableRowAutomationPeer.ControlType);
        var patterns = typeof(SaDataTableRowAutomationPeer).GetMethod("GetPatternCore", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(patterns);
        Assert.Equal(typeof(SaDataTableRowAutomationPeer), patterns!.DeclaringType);   // overridden: returns no pattern
        Assert.Equal(typeof(SaDataTableRow), typeof(SaDataTableRow).GetMethod("OnCreateAutomationPeer", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        // "x of N": position and size come from the hosting ItemsRepeater (Cortex M-2).
        foreach (var core in new[] { "GetPositionInSetCore", "GetSizeOfSetCore" })
        {
            Assert.Equal(typeof(SaDataTableRowAutomationPeer), typeof(SaDataTableRowAutomationPeer).GetMethod(core, BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        }

        Assert.Equal(AutomationControlType.List, SaDataTableListAutomationPeer.ControlType);
        Assert.Equal(typeof(SaDataTableListAutomationPeer), typeof(SaDataTableListAutomationPeer).GetMethod("GetPatternCore", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
    }

    [Fact]
    public void DefaultStyle_IsFocusable_AndHasNoInteractiveVisualStates()
    {
        var style = Assert.Single(AppSourceTree.LoadXaml("Styles/Components/Sa.Primitives.xaml").Descendants(),
            e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "primitives:SaDataTableRow");
        var setters = style.Elements().Where(e => e.Name.LocalName == "Setter")
            .ToDictionary(s => (string)s.Attribute("Property")!, s => (string?)s.Attribute("Value") ?? string.Empty);

        Assert.Equal("True", setters["IsTabStop"]);
        Assert.Equal("True", setters["UseSystemFocusVisuals"]);
        Assert.Equal("64", setters["MinHeight"]);
        // Prism R2-1: the ring goes OUT 8 horizontally (never over the dot or the last glyph) and IN 2 vertically.
        Assert.Equal("-8,2,-8,2", setters["FocusVisualMargin"]);
        Assert.Equal("{ThemeResource SaFocusRingBrush}", setters["FocusVisualPrimaryBrush"]);
        Assert.DoesNotContain(style.Descendants(), e => e.Name.LocalName == "VisualState");   // no hover/pressed: not a button
    }

    [Fact]
    public void FocusedRow_IsBroughtIntoView_ForKeyboardAndProgrammaticFocus()
    {
        // Prism R2 / Cortex M-3: in the stacked layouts a row focused off-screen (UIA SetFocus or Tab/arrows) must scroll
        // the page to it. WinUI does not do that for programmatic focus, so the row asks on every GotFocus.
        var gotFocus = typeof(SaDataTableRow).GetMethod("OnGotFocus", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(typeof(SaDataTableRow), gotFocus!.DeclaringType);
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaDataTableRow.cs");
        var body = code[code.IndexOf("OnGotFocus", StringComparison.Ordinal)..];
        Assert.Contains("StartBringIntoView(", body[..body.IndexOf('}')], StringComparison.Ordinal);
    }

    [Fact]
    public void RowNames_AreTheFullRow_NameDetailStateAndHealthOrStartup()
    {
        var pt = new ResWLocalizationService("pt-PT");
        var container = new ContainerRowViewModel(new ContainerInfo
        {
            ContainerId = "a", Name = "worker", Image = "serveralyzer/worker:1.4", State = ContainerState.Running,
            StatusText = "Up", Health = ContainerHealth.Unhealthy
        }, pt);
        Assert.Equal("worker, imagem serveralyzer/worker:1.4, container Em execução, saúde Não saudável", container.DisplayAutomationName);

        var service = new ServiceRowViewModel(new ServiceInfo
        {
            Id = "backup.service", Name = "backup", DisplayName = "Cópia de segurança", State = ServiceState.Failed,
            StartupState = ServiceStartupState.Enabled
        }, pt);
        Assert.Equal("backup, Cópia de segurança, Falhou, arranque Automático", service.DisplayAutomationName);

        var bare = new ServiceRowViewModel(new ServiceInfo { Id = "x.service", Name = "x", State = ServiceState.Running }, new ResWLocalizationService("en-US"));
        Assert.Equal("x, Active, startup unknown", bare.DisplayAutomationName);
    }
}
