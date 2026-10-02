using ServerMonitor.App.Controls;

namespace ServerMonitor.App.Tests.Controls;

/// <summary>UI.3: the History chart's axis/grid layout (Figma 112:2298) is pure and decided here, not in the canvas.</summary>
public sealed class HistoryChartAxisLayoutTests
{
    [Fact]
    public void GridLines_AreTheThreeFigmaLines_100_50_0()
    {
        Assert.Equal(new[] { 0d, 49d, 98d }, HistoryChartAxisLayout.GridLineYs(98));
        Assert.Empty(HistoryChartAxisLayout.GridLineYs(0));
    }

    [Fact]
    public void XLabels_SitOnTheQuartiles_AndStayInsideThePlot()
    {
        const double plot = 774;
        const double label = 30;

        Assert.Equal(0, HistoryChartAxisLayout.XLabelLeft(0, 5, label, plot));                    // first: left edge
        Assert.Equal(plot / 4 - label / 2, HistoryChartAxisLayout.XLabelLeft(1, 5, label, plot));  // centred on the tick
        Assert.Equal(plot / 2 - label / 2, HistoryChartAxisLayout.XLabelLeft(2, 5, label, plot));
        Assert.Equal(plot - label, HistoryChartAxisLayout.XLabelLeft(4, 5, label, plot));          // last: ends at right edge
        Assert.Equal(0, HistoryChartAxisLayout.XLabelLeft(0, 1, label, plot));
        Assert.Equal(0, HistoryChartAxisLayout.XLabelLeft(3, 5, label, 0));
    }

    [Fact]
    public void YLabels_AreCentredOnTheirGridLine()
    {
        Assert.Equal(-7, HistoryChartAxisLayout.YLabelTop(0, 3, 14, 98));
        Assert.Equal(42, HistoryChartAxisLayout.YLabelTop(1, 3, 14, 98));
        Assert.Equal(91, HistoryChartAxisLayout.YLabelTop(2, 3, 14, 98));
    }

    [Fact]
    public void CompactLabels_OnlyBelowTheThreshold_AndOnlyWhenProvided()
    {
        IReadOnlyList<string> full = ["a", "b", "c", "d", "e"];
        IReadOnlyList<string> compact = ["a", "c", "e"];

        Assert.Same(compact, HistoryChartAxisLayout.ChooseXLabels(400, full, compact, 480));
        Assert.Same(full, HistoryChartAxisLayout.ChooseXLabels(480, full, compact, 480));
        Assert.Same(full, HistoryChartAxisLayout.ChooseXLabels(400, full, [], 480));
        Assert.Empty(HistoryChartAxisLayout.ChooseXLabels(400, null, null, 480));
    }
}
