using System.Reflection;
using ServerMonitor.App.Controls;
using ServerMonitor.App.Controls.Primitives;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.4 Beacon QA r1 contracts that need a XAML runtime to observe (no UI test host here), held on the code instead:
/// both server lists say "x of N" (SHOULD-2) and the pages put focus on content / the origin row (SHOULD-1/5).
/// </summary>
public sealed class Ui4BeaconContractTests
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>SHOULD-2: the row of each list creates a peer that answers PositionInSet / SizeOfSet itself.</summary>
    [Theory]
    [InlineData(typeof(SaListRow), typeof(SaListRowAutomationPeer))]
    [InlineData(typeof(ServerTableRowButton), typeof(ServerTableRowButtonAutomationPeer))]
    [InlineData(typeof(SaDataTableRow), typeof(SaDataTableRowAutomationPeer))]
    public void ListRows_ExposePositionAndSizeOfSet(Type row, Type peer)
    {
        Assert.NotNull(row.GetMethod("OnCreateAutomationPeer", Declared));
        Assert.NotNull(peer.GetMethod("GetPositionInSetCore", Declared));
        Assert.NotNull(peer.GetMethod("GetSizeOfSetCore", Declared));
    }

    /// <summary>SHOULD-2: the Servidores rows ARE that row type (a plain Button would answer -1/-1 again).</summary>
    [Fact]
    public void ServersRows_UseTheRowButtonWithPosition()
    {
        var rows = AppSourceTree.LoadXaml("Views/ServersPage.xaml").Descendants()
            .Where(e => (string?)e.Attribute("Style") == "{StaticResource ServerRowButtonStyle}").ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(nameof(ServerTableRowButton), row.Name.LocalName));
    }

    /// <summary>SHOULD-1: the Visão geral takes the return target after loading and otherwise focuses its content.</summary>
    [Fact]
    public void Overview_FocusesTheReturnTargetOrTheContent_AfterLoading()
    {
        var code = AppSourceTree.CodeWithoutComments("Views/DashboardPage.xaml.cs");
        var load = code.IndexOf("await ViewModel.LoadAsync()", StringComparison.Ordinal);
        Assert.True(load >= 0);
        Assert.True(code.IndexOf("RestoreFocus", load, StringComparison.Ordinal) > load, "focus is placed after the load");
        Assert.Contains("ViewModel.TakeReturnFocus()", code);
        Assert.Contains("ShellPageFocus.FocusHeading(this)", code);
        Assert.Contains("ShellPageFocus.GetKeepSidebar(this)", code);
        Assert.Contains("PriorityButton.Focus(", code);
        Assert.Contains("DirectoryLinkButton.Focus(", code);
    }

    /// <summary>SHOULD-1/5: Servidores refocuses the origin row on Loaded and the focused row across the width reflow.</summary>
    [Fact]
    public void Servers_RefocusesTheOriginRow_AndKeepsFocusAcrossTheReflow()
    {
        var code = AppSourceTree.CodeWithoutComments("Views/ServersPage.xaml.cs");
        Assert.Contains("ViewModel.TakeReturnFocusIndex()", code);
        // Prism r2 C-R2-1: with no server, "Adicionar servidor" rather than an empty search.
        Assert.Contains("ShellPageFocus.GetKeepSidebar(this)", code);
        Assert.Contains("ShellPageFocus.FocusHeading(this)", code);
        // AdaptiveTrigger changes never raise CurrentStateChanging/Changed (QA r2): the template swap is observed instead.
        Assert.Contains("ServersRepeater.RegisterPropertyChangedCallback(ItemsRepeater.ItemTemplateProperty, OnRowTemplateChanged)", code);
        Assert.Contains("ServersRepeater.GotFocus += OnRowGotFocus", code);
        Assert.DoesNotContain("CurrentStateChanging", code);
    }

    /// <summary>
    /// QA r2 (Light after a runtime switch): a ThemeResource in an active visual state's setter is not re-resolved, so the
    /// status dot must re-enter its state when the effective theme changes — also on Loaded (a cached page is out of the
    /// tree while the theme changes in Definições).
    /// </summary>
    [Fact]
    public void StatusIndicator_ReappliesItsStateOnThemeChange()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaStatusIndicator.cs");
        Assert.Contains("ActualThemeChanged += (_, _) => RefreshStatusStateForTheme();", code);
        Assert.Contains("Loaded += (_, _) => RefreshStatusStateForTheme();", code);
        Assert.Contains("_stateTheme = ActualTheme;", code);
    }
}
