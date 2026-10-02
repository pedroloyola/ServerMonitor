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
        Assert.Contains("HealthCard.Focus(", code);
        Assert.Contains("EmptyAddButton.Focus(", code);
        Assert.Contains("PriorityButton.Focus(", code);
        Assert.Contains("ViewAllButton.Focus(", code);
    }

    /// <summary>SHOULD-1/5: Servidores refocuses the origin row on Loaded and the focused row across the width reflow.</summary>
    [Fact]
    public void Servers_RefocusesTheOriginRow_AndKeepsFocusAcrossTheReflow()
    {
        var code = AppSourceTree.CodeWithoutComments("Views/ServersPage.xaml.cs");
        Assert.Contains("ViewModel.TakeReturnFocusIndex()", code);
        Assert.Contains("WidthStates.CurrentStateChanging +=", code);
        Assert.Contains("WidthStates.CurrentStateChanged +=", code);
        Assert.Contains("RepeaterFocus.FocusedIndex(ServersRepeater)", code);
    }
}
