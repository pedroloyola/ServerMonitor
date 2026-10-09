using ServerMonitor.App.ViewModels;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.10 HD-2 mitigation (Boss/human decision 2026-10-09: Crítico and Sem ligação keep the Manual's shared colour): the
/// health bar's segments run in exactly the chips' order after the healthy run, so a reader matches a segment to the
/// chip that names it by position (and the bar's accessible name lists every count - Ui4OverviewTests).
/// </summary>
public sealed class Ui10HealthBarOrderTests
{
    [Fact]
    public void Segments_FollowTheChipsOrder_AfterTheHealthyRun()
    {
        Assert.Equal(ServerHealth.Healthy, OverviewPresentation.SegmentOrder[0]);
        Assert.Equal(OverviewPresentation.ChipOrder, OverviewPresentation.SegmentOrder.Skip(1));
    }
}
