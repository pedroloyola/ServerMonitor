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
    public void FocusableCard_IsAReadOnlyNamedGroup_OneTabStop_BroughtIntoViewOnFocus()
    {
        // Beacon F3: History chart cards are reachable by keyboard in short windows, without becoming interactive.
        Assert.Equal(AutomationControlType.Group, SaFocusableCardAutomationPeer.ControlType);
        Assert.Equal(typeof(SaFocusableCardAutomationPeer), typeof(SaFocusableCardAutomationPeer).GetMethod("GetPatternCore", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        Assert.Equal(typeof(SaFocusableCard), typeof(SaFocusableCard).GetMethod("OnGotFocus", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaFocusableCard.cs");
        var body = code[code.IndexOf("OnGotFocus", StringComparison.Ordinal)..];
        Assert.Contains("StartBringIntoView(", body[..body.IndexOf('}')], StringComparison.Ordinal);

        var style = Assert.Single(AppSourceTree.LoadXaml("Styles/Components/Sa.Primitives.xaml").Descendants(),
            e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "primitives:SaFocusableCard");
        var setters = style.Elements().Where(e => e.Name.LocalName == "Setter")
            .ToDictionary(s => (string)s.Attribute("Property")!, s => (string?)s.Attribute("Value") ?? string.Empty);
        Assert.Equal("True", setters["IsTabStop"]);
        Assert.Equal("True", setters["UseSystemFocusVisuals"]);
        Assert.Equal("{ThemeResource SaFocusRingBrush}", setters["FocusVisualPrimaryBrush"]);
        Assert.Equal("{StaticResource SaFocusRingThickness}", setters["FocusVisualPrimaryThickness"]);
        Assert.DoesNotContain(style.Descendants(), e => e.Name.LocalName == "VisualState");
    }

    [Fact]
    public void Selectors_ShowTheSaFocusRing()
    {
        // Beacon F2 (WCAG 2.4.7): SaSelectorFieldStyle (base of Pill and Rich) draws the 2 px SaFocusRing.
        var forms = AppSourceTree.LoadXaml("Styles/Components/Sa.Forms.xaml").Descendants().Where(e => e.Name.LocalName == "Style").ToList();
        var field = forms.Single(s => (string?)s.Attribute(Architecture.AppSourceTree.Xaml + "Key") == "SaSelectorFieldStyle");
        var setters = field.Elements().Where(e => e.Name.LocalName == "Setter")
            .ToDictionary(s => (string)s.Attribute("Property")!, s => (string?)s.Attribute("Value") ?? string.Empty);
        Assert.Equal("True", setters["UseSystemFocusVisuals"]);
        Assert.Equal("{ThemeResource SaFocusRingBrush}", setters["FocusVisualPrimaryBrush"]);
        Assert.Equal("{StaticResource SaFocusRingThickness}", setters["FocusVisualPrimaryThickness"]);
        Assert.Equal("0", setters["FocusVisualSecondaryThickness"]);
        foreach (var key in new[] { "SaSelectorPillStyle", "SaSelectorRichStyle" })
        {
            var derived = forms.Single(s => (string?)s.Attribute(Architecture.AppSourceTree.Xaml + "Key") == key);
            Assert.Equal("{StaticResource SaSelectorFieldStyle}", (string?)derived.Attribute("BasedOn"));
            Assert.DoesNotContain(derived.Elements(), s => ((string?)s.Attribute("Property"))?.StartsWith("FocusVisual", StringComparison.Ordinal) == true
                                                              || (string?)s.Attribute("Property") == "UseSystemFocusVisuals");
        }
    }

    [Fact]
    public void EmptyState_IsNamedByItsTitle_NotByItsAction()
    {
        // Beacon L2: the no-results panel was announced as "Limpar pesquisa" (name derived from the button).
        Assert.Equal(AutomationControlType.Group, SaEmptyStateAutomationPeer.ControlType);
        Assert.Equal(typeof(SaEmptyStateAutomationPeer), typeof(SaEmptyStateAutomationPeer).GetMethod("GetNameCore", BindingFlags.Instance | BindingFlags.NonPublic)!.DeclaringType);
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaEmptyState.cs");
        Assert.Contains(").Title", code[code.IndexOf("GetNameCore", StringComparison.Ordinal)..], StringComparison.Ordinal);
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
