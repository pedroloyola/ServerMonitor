using System.Text.RegularExpressions;
using ServerMonitor.App.Controls.Primitives;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.12 B12B-02 (Beacon re-QA, P3, UIA listener): the "Servidor ocultado" toast - made Visible by x:Bind while the app
/// navigated Detail → Servers, before the page was on the live tree - raised 0 LiveRegionChanged (the queued raise found
/// it not live and dropped it; the other toasts raise 1). The toast now catches up on Loaded, once, unless it was already
/// said. Rule pure + wiring by source; the event count stays Beacon's (listener ≠ Narrator).
/// </summary>
public sealed partial class Ui12ToastAnnouncementTests
{
    private const string Hidden = "Servidor ocultado. Podes mostrar prod-web-01 de novo nas Definições.";

    [Fact]
    public void VisibleBeforeLoaded_TheDroppedRaiseIsMadeUpOnLoaded_Once()
    {
        var toast = new ToastAnnouncement();
        toast.ContentChanged(Hidden);   // x:Bind sets the message ...
        toast.VisibilityChanged(true);  // ... and the visibility, before Loaded: the queued raise is dropped (not live)

        Assert.True(toast.ShouldAnnounceOnLoaded(visible: true));
        toast.Announced();              // the Loaded raise left
        Assert.False(toast.ShouldAnnounceOnLoaded(visible: true)); // a later Loaded (navigating back) never repeats it
    }

    [Fact]
    public void AToastAlreadySaid_IsNotRepeatedOnLoaded()
    {
        var toast = new ToastAnnouncement();
        toast.ContentChanged(Hidden);
        toast.VisibilityChanged(true);
        toast.Announced();              // shown on a live page: said by the visibility raise

        Assert.False(toast.ShouldAnnounceOnLoaded(visible: true));
    }

    [Fact]
    public void AClosedToast_IsNeverAnnouncedOnLoaded_AndTheNextShowingIsNew()
    {
        var toast = new ToastAnnouncement();
        toast.ContentChanged(Hidden);
        toast.VisibilityChanged(true);
        toast.Announced();
        toast.VisibilityChanged(false);

        Assert.False(toast.ShouldAnnounceOnLoaded(visible: false));
        toast.VisibilityChanged(true);
        Assert.True(toast.ShouldAnnounceOnLoaded(visible: true));
    }

    [Fact]
    public void OnlyNewContent_NeedsSayingAgain()
    {
        var toast = new ToastAnnouncement();
        toast.ContentChanged(Hidden);
        toast.VisibilityChanged(true);
        toast.Announced();

        toast.ContentChanged(Hidden);   // the same text set again (a template re-applied)
        Assert.False(toast.ShouldAnnounceOnLoaded(visible: true));
        toast.ContentChanged("Guardado.");
        Assert.True(toast.ShouldAnnounceOnLoaded(visible: true));
    }

    [Fact]
    public void TheToast_CatchesUpOnLoaded_AndRecordsWhatWasActuallySaid()
    {
        var toast = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaToast.cs");
        var announce = AppSourceTree.CodeWithoutComments("Controls/Primitives/SaAnnounce.cs");

        Assert.Matches(CatchUpOnLoaded(), toast);
        Assert.Contains("_announcement.ContentChanged(name);", toast, StringComparison.Ordinal);
        // UI.12 B12B-02-R: the three raises (visibility, Loaded, content) go through AnnounceLive, the one raise site.
        Assert.Equal(3, Regex.Matches(toast, Regex.Escape("AnnounceLive();")).Count);
        Assert.Single(Regex.Matches(toast, Regex.Escape("SaAnnounce.RaiseAfterLayout(this, _announcement.Announced);")));
        Assert.DoesNotContain("SaAnnounce.RaiseAfterLayout(this);", toast, StringComparison.Ordinal);
        Assert.Matches(ReportsOnlyARaiseThatLeft(), announce);
    }

    [GeneratedRegex(@"Loaded \+= \(_, _\) =>\s*\{\s*if \(_announcement\.ShouldAnnounceOnLoaded\(Visibility == Visibility\.Visible\)\)\s*\{\s*AnnounceLive\(\);")]
    private static partial Regex CatchUpOnLoaded();

    [GeneratedRegex(@"if \(ShouldAnnounce\(element\.Visibility == Visibility\.Visible, SaLiveTree\.IsLive\(element\), ContentOf\(element\)\)\)\s*\{\s*Raise\(element\);\s*announced\?\.Invoke\(\);\s*\}")]
    private static partial Regex ReportsOnlyARaiseThatLeft();
}
