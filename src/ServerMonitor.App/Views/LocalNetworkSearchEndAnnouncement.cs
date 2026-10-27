using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Services;

namespace ServerMonitor.App.Views;

/// <summary>
/// SVA-14 (L-4): the end of a user-started local-network search is said once, as the caption the page now shows. The
/// captions are not live regions (SaAnnounce.Changes would speak again on every page re-entry); the view model's
/// LocalNetworkSearchEnded is the one trigger, and each page raises it on ITS visible caption.
/// </summary>
internal static class LocalNetworkSearchEndAnnouncement
{
    /// <summary>Fixed, never localized: the "most recent" source of the announcement.</summary>
    internal const string ActivityId = "LocalNetworkSearchEnded";

    /// <summary>Pure rule (unit-tested): the caption that says this end; none for a stop or no end.</summary>
    internal static T? CaptionFor<T>(DiscoverySearchEnd end, T nothingFound, T timeLimit, T failed) where T : class =>
        end switch
        {
            DiscoverySearchEnd.NothingFound => nothingFound,
            DiscoverySearchEnd.TimeLimit => timeLimit,
            DiscoverySearchEnd.Failed => failed,
            _ => null,
        };

    /// <summary>
    /// In a later turn, after the layout pass that shows the caption (B12B-02-R: a polite raise in the same turn as a
    /// focus change is dropped), and only if it is shown and on the live tree then (a page left meanwhile says nothing).
    /// </summary>
    internal static void AnnounceAfterLayout(TextBlock? caption) =>
        caption?.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (SaAnnounce.ShouldAnnounce(caption.Visibility == Visibility.Visible, SaLiveTree.IsLive(caption), caption.Text))
            {
                (FrameworkElementAutomationPeer.FromElement(caption) ?? FrameworkElementAutomationPeer.CreatePeerForElement(caption))
                    ?.RaiseNotificationEvent(
                        AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.CurrentThenMostRecent,
                        caption.Text, ActivityId);
            }
        });
}
