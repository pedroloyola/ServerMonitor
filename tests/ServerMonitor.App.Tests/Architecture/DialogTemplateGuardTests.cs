using System.Xml.Linq;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Controls.Primitives;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.2 R1 (Cortex R-2, Prism MF-3, Beacon F-1). <c>SaDialogStyle</c> owns its ContentDialog template so the default
/// button keeps the Sa look (the Fluent template swaps in AccentButtonStyle - the legacy #1846E1), while ContentDialog's
/// own code still finds every part and state it drives. Destructive dialogs default to the safe button.
/// </summary>
public sealed class DialogTemplateGuardTests
{
    private const string DialogsFile = "Styles/Components/Sa.Dialogs.xaml";

    /// <summary>The Fluent ContentDialog template's part names (WinAppSDK 2.3 generic.xaml) the control code resolves.</summary>
    private static readonly string[] FluentParts =
    [
        "Container", "LayoutRoot", "BackgroundElement", "ScaleTransform", "DialogSpace", "ContentScrollViewer", "Title",
        "Content", "CommandSpace", "PrimaryButton", "SecondaryButton", "CloseButton"
    ];

    /// <summary>The Fluent template's visual-state groups and states, which ContentDialog's code moves through.</summary>
    private static readonly Dictionary<string, string[]> FluentStates = new(StringComparer.Ordinal)
    {
        ["DialogShowingStates"] = ["DialogHidden", "DialogShowing", "DialogShowingWithoutSmokeLayer"],
        ["DialogSizingStates"] = ["DefaultDialogSizing", "FullDialogSizing"],
        ["ButtonsVisibilityStates"] =
        [
            "AllVisible", "NoneVisible", "PrimaryVisible", "SecondaryVisible", "CloseVisible", "PrimaryAndSecondaryVisible",
            "PrimaryAndCloseVisible", "SecondaryAndCloseVisible"
        ],
        ["DefaultButtonStates"] = ["NoDefaultButton", "PrimaryAsDefaultButton", "SecondaryAsDefaultButton", "CloseAsDefaultButton"],
        ["DialogBorderStates"] = ["NoBorder", "AccentColorBorder"]
    };

    private static XElement Template() => AppSourceTree.LoadXaml(DialogsFile).Descendants()
        .Single(e => e.Name.LocalName == "ControlTemplate" && (string?)e.Attribute("TargetType") == "ContentDialog");

    [Fact]
    public void TheTemplateKeepsEveryFluentPartAndState()
    {
        var template = Template();
        var names = template.Descendants().Select(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        Assert.All(FluentParts, part => Assert.Contains(part, names));

        var groups = template.Descendants().Where(e => e.Name.LocalName == "VisualStateGroup")
            .ToDictionary(g => (string)g.Attribute(AppSourceTree.Xaml + "Name")!,
                g => g.Elements().Where(s => s.Name.LocalName == "VisualState").Select(s => (string)s.Attribute(AppSourceTree.Xaml + "Name")!).ToArray(),
                StringComparer.Ordinal);
        foreach (var (group, states) in FluentStates)
        {
            Assert.True(groups.ContainsKey(group), $"missing VisualStateGroup {group}");
            Assert.Equal(states.Order(StringComparer.Ordinal), groups[group].Order(StringComparer.Ordinal));
        }
    }

    /// <summary>No setter anywhere in the dialog dictionary applies AccentButtonStyle, and the default-button states are empty.</summary>
    [Fact]
    public void TheDefaultButtonNeverWearsTheLegacyAccent()
    {
        var document = AppSourceTree.LoadXaml(DialogsFile);
        var accentUses = document.Descendants().SelectMany(e => e.Attributes())
            .Where(a => a.Value.Contains("AccentButton", StringComparison.Ordinal)).Select(a => $"{a.Parent!.Name.LocalName}.{a.Name.LocalName}={a.Value}").ToList();
        Assert.True(accentUses.Count == 0, string.Join(Environment.NewLine, accentUses));

        var defaultStates = Template().Descendants()
            .Single(e => e.Name.LocalName == "VisualStateGroup" && (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "DefaultButtonStates")
            .Elements().Where(s => s.Name.LocalName == "VisualState").ToList();
        Assert.Equal(4, defaultStates.Count);
        Assert.All(defaultStates, state => Assert.False(state.HasElements, $"{state.Attribute(AppSourceTree.Xaml + "Name")} must stay empty"));
    }

    /// <summary>Prism MF-3: one surface, buttons right-aligned with a 12 gap (not the stretched Fluent command grid).</summary>
    [Fact]
    public void CommandButtonsAreRightAlignedAndNotStretched()
    {
        var commandSpace = Template().Descendants().Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "CommandSpace");
        var row = commandSpace.Elements().Single(e => e.Name.LocalName == "StackPanel");
        Assert.Equal("Horizontal", (string?)row.Attribute("Orientation"));
        Assert.Equal("Right", (string?)row.Attribute("HorizontalAlignment"));
        Assert.Equal("12", (string?)row.Attribute("Spacing"));
        Assert.Null(commandSpace.Attribute("Background"));
        Assert.All(row.Elements(), button => Assert.Null(button.Attribute("HorizontalAlignment")));
    }

    [Fact]
    public void DestructiveDialogsDefaultToTheSafeButton()
    {
        var destructive = SaDialog.BehaviourFor(SaDialogKind.Destructive);
        Assert.Equal(ContentDialogButton.Close, destructive.DefaultButton);
        Assert.Equal("CloseButton", destructive.InitialFocusPart);
        Assert.True(destructive.DestructivePrimary);

        var confirm = SaDialog.BehaviourFor(SaDialogKind.Confirm);
        Assert.Equal(ContentDialogButton.Primary, confirm.DefaultButton);
        Assert.Equal("PrimaryButton", confirm.InitialFocusPart);
        Assert.False(confirm.DestructivePrimary);
    }

    [Theory]
    [InlineData(SaDialogKind.Destructive)]
    [InlineData(SaDialogKind.Confirm)]
    public void TheInitialFocusPartExistsInTheTemplate(SaDialogKind kind) =>
        Assert.Contains(Template().Descendants(), e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == SaDialog.BehaviourFor(kind).InitialFocusPart);
}
