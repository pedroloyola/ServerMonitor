using ServerMonitor.App.Controls;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.8 fix round c4, Cortex c2 C2-2: the Compact row's name tooltip follows the CURRENT name. A recycled row whose name goes
/// from one cut text to another raises no IsTextTrimmedChanged (the trim state did not change), so the tooltip is also
/// refreshed on a text change of the same element - through the one rule, CompactNameTooltip.For.
/// </summary>
public sealed class Ui8CompactC4Tests
{
    [Theory]
    [InlineData(true, "production-postgresql-primary-eu-west-1a.internal.example", "production-postgresql-primary-eu-west-1a.internal.example")]
    [InlineData(false, "x", null)]
    [InlineData(true, "", null)]
    [InlineData(true, null, null)]
    public void TheTooltip_IsTheCurrentName_OnlyWhileItIsCut(bool trimmed, string? text, string? expected) =>
        Assert.Equal(expected, CompactNameTooltip.For(trimmed, text));

    [Fact]
    public void ARecycledName_RefreshesTheTooltipOnATextChange_AsOnATrimChange()
    {
        var name = AppSourceTree.LoadXaml("Controls/CompactShell.xaml").Descendants()
            .Single(e => (string?)e.Attribute("IsTextTrimmedChanged") == "OnNameTrimmedChanged");
        Assert.Equal("OnNameLoaded", (string?)name.Attribute("Loaded"));

        var code = AppSourceTree.CodeWithoutComments("Controls/CompactShell.xaml.cs");
        Assert.Contains("private void OnNameTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) => UpdateNameTooltip(sender);", code, StringComparison.Ordinal);
        Assert.Contains("name.RegisterPropertyChangedCallback(TextBlock.TextProperty, (element, _) => UpdateNameTooltip((TextBlock)element));", code, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.SetToolTip(row, CompactNameTooltip.For(name.IsTextTrimmed, name.Text));", code, StringComparison.Ordinal);
        // One callback per element (a re-load never stacks a second one).
        Assert.Contains("!NameTextWatched.TryGetValue(name, out _)", code, StringComparison.Ordinal);
    }
}
