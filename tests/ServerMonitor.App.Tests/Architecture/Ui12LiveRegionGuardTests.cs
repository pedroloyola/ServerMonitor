using System.Text.RegularExpressions;
using ServerMonitor.App.Controls.Primitives;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 LIVE-01 (Prism UI12A): a declared live region is announced only when the app raises LiveRegionChanged on it
/// (documented: the event is the change notification; AutomationProperties.LiveSetting is only its politeness). Static
/// guard over the sources + the pure announce rule; whether Narrator speaks each one stays a human check (Beacon R-7).
/// </summary>
public sealed partial class Ui12LiveRegionGuardTests
{
    /// <summary>
    /// Declarative live TextBlocks raised elsewhere: PART_Error (SaFormField raises it), RefreshingText (ServerDetailPage
    /// code-behind), RouteLine (rewritten on every keystroke of the jump-host fields: announcing each would be noise),
    /// and the editor page, which is Sentry's in UI.12 (handover in the Relay log).
    /// </summary>
    private static readonly (string File, string? Name)[] RaisedElsewhere =
    [
        ("Styles/Components/Sa.Primitives.xaml", "PART_Error"),
        ("Views/ServerDetailPage.xaml", "RefreshingText"),
        ("Controls/ServerFormControl.xaml", "RouteLine"),
        ("Views/ServerEditorPage.xaml", null),
    ];

    [Fact]
    public void EveryDeclarativeLiveTextBlockAnnouncesItsChanges()
    {
        var failures = new List<string>();
        var checkedBlocks = 0;
        foreach (var file in AppSourceTree.Files(".xaml").Where(f => !f.StartsWith("Qa/", StringComparison.Ordinal)))
        {
            foreach (var block in AppSourceTree.LoadXaml(file).Descendants().Where(e => e.Name.LocalName == "TextBlock"
                         && e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.LiveSetting")))
            {
                var name = (string?)block.Attribute(AppSourceTree.Xaml + "Name");
                if (RaisedElsewhere.Any(x => x.File == file && (x.Name is null || x.Name == name)))
                {
                    continue;
                }

                checkedBlocks++;
                if (!block.Attributes().Any(a => a.Name.LocalName == "SaAnnounce.Changes" && a.Value == "True"))
                {
                    failures.Add($"{file}: TextBlock {name ?? (string?)block.Attribute(AppSourceTree.Xaml + "Uid") ?? (string?)block.Attribute("Text") ?? "<unnamed>"} declares LiveSetting without SaAnnounce.Changes");
                }
            }
        }

        Assert.True(checkedBlocks >= 10, "Too few live TextBlocks found; the guard would be vacuous.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>FromElement alone returns null for an element that never had a peer (a part that was Collapsed): the
    /// event must go through SaAnnounce, or at least fall back to CreatePeerForElement.</summary>
    [Fact]
    public void LiveRegionEventsNeverDependOnAnExistingPeerOnly()
    {
        var failures = AppSourceTree.Files(".cs")
            .Where(f => f != "Views/ServerEditorPage.xaml.cs") // Sentry-owned in UI.12; handover in the Relay log
            .Where(f => BarePeerRaise().IsMatch(AppSourceTree.CodeWithoutComments(f)))
            .ToList();

        Assert.True(failures.Count == 0, "FromElement(...)?.RaiseAutomationEvent without a peer fallback: " + string.Join(", ", failures));
    }

    [Fact]
    public void TransientPrimitivesAnnounceWhenShown()
    {
        var toast = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaToast.cs");
        var notice = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaInlineNotice.cs");
        var field = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaFormField.cs");

        Assert.Contains("protected override AutomationPeer OnCreateAutomationPeer() => new SaToastAutomationPeer(this);", toast, StringComparison.Ordinal);
        foreach (var code in new[] { toast, notice })
        {
            Assert.Contains("RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => SaAnnounce.RaiseAfterLayout(this));", code, StringComparison.Ordinal);
        }

        Assert.Contains("SaAnnounce.RaiseAfterLayout(_error);", field, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetFullDescription(input, ErrorText);", field, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreErrorIsTheDescriptionOfThePassphraseBox()
    {
        var box = AppSourceTree.LoadXaml("Views/RestoreOpenDialog.xaml").Descendants()
            .Single(e => (string?)e.Attribute(AppSourceTree.Xaml + "Name") == "PassphraseBox");
        Assert.Equal("{x:Bind Session.ErrorMessage, Mode=OneWay}", (string?)box.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.FullDescription"));
    }

    [Theory]
    [InlineData(true, true, "Servidor removido.", true)]
    [InlineData(false, true, "Servidor removido.", false)]
    [InlineData(true, false, "Servidor removido.", false)]
    [InlineData(true, true, "", false)]
    [InlineData(true, true, "   ", false)]
    [InlineData(true, true, null, false)]
    public void OnlyAShownConnectedRegionWithContentIsAnnounced(bool visible, bool live, string? content, bool expected) =>
        Assert.Equal(expected, SaAnnounce.ShouldAnnounce(visible, live, content));

    [GeneratedRegex(@"FromElement\([^()]*\)\s*\?\.\s*RaiseAutomationEvent")]
    private static partial Regex BarePeerRaise();
}
