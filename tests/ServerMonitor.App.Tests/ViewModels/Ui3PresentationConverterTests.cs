using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Converters;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>UI.3 phase 2 converters (pure parts; no XAML runtime).</summary>
public sealed class Ui3PresentationConverterTests
{
    [Theory]
    [InlineData(2, "2", true)]
    [InlineData(2, "3", false)]
    [InlineData(0, "0", true)]
    [InlineData(4, "x", false)]
    public void IndexToBoolean_ChecksTheMatchingSegment(int index, string parameter, bool expected)
    {
        Assert.Equal(expected, new IndexToBooleanConverter().Convert(index, typeof(bool), parameter, "pt-PT"));
    }

    [Fact]
    public void IndexToBoolean_CheckingASegment_WritesItsIndexBack()
    {
        Assert.Equal(3, new IndexToBooleanConverter().ConvertBack(true, typeof(int), "3", "pt-PT"));
        Assert.True(IndexToBooleanConverter.TryParse("4", out var index));
        Assert.Equal(4, index);
        Assert.False(IndexToBooleanConverter.TryParse(null, out _));
    }

    [Theory]
    [InlineData(WorkloadSeverity.Positive, SaStatusKind.Healthy)]
    [InlineData(WorkloadSeverity.Negative, SaStatusKind.Attention)]   // D-UI3-2: never the red error colour
    [InlineData(WorkloadSeverity.Warning, SaStatusKind.Attention)]
    [InlineData(WorkloadSeverity.Neutral, SaStatusKind.Unknown)]
    public void WorkloadDot_UsesAttentionForProblems(WorkloadSeverity severity, SaStatusKind expected)
    {
        Assert.Equal(expected, WorkloadSeverityToStatusKindConverter.For(severity));
        Assert.Equal(SaStatusKind.Unknown, WorkloadSeverityToStatusKindConverter.For(null));
    }

    [Theory]
    [InlineData(WorkloadSeverity.Negative, null, "SaWorkloadStateAttentionTextStyle")]
    [InlineData(WorkloadSeverity.Negative, "Detail", "SaWorkloadDetailAttentionTextStyle")]
    [InlineData(WorkloadSeverity.Warning, null, "SaWorkloadStateAttentionTextStyle")]
    [InlineData(WorkloadSeverity.Positive, null, "SaWorkloadStateTextStyle")]
    [InlineData(WorkloadSeverity.Positive, "Detail", "SaWorkloadDetailTextStyle")]
    [InlineData(WorkloadSeverity.Neutral, "Detail", "SaWorkloadDetailTextStyle")]
    public void ProblemRows_HaveBothStateLinesInAttention(WorkloadSeverity severity, string? line, string expected)
    {
        // Figma 112:2666: an unhealthy running container reads "Em execução" AND "Não saudável" in attention.
        Assert.Equal(expected, WorkloadSeverityToSaTextStyleConverter.KeyFor(severity, line));
    }
}
