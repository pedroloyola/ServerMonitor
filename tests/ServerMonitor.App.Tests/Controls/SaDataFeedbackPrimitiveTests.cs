using System.Reflection;
using Microsoft.UI.Xaml;
using ServerMonitor.App.Controls.Primitives;

namespace ServerMonitor.App.Tests.Controls;

/// <summary>
/// UI.2 S6. Behaviour of the data/feedback primitives that does not need a XAML runtime: an unknown reading is never
/// shown as zero, the skeleton pulse obeys the system animation setting, and every enum value has its visual state.
/// </summary>
public sealed class SaDataFeedbackPrimitiveTests
{
    [Theory]
    [InlineData(double.NaN, 100, "—")]
    [InlineData(0, 100, "0%")]
    [InlineData(22, 100, "22%")]
    [InlineData(93.4, 100, "93%")]
    [InlineData(150, 100, "100%")]
    [InlineData(5, 0, "—")]
    public void MetricBarTextNeverShowsAnUnknownReadingAsZero(double value, double maximum, string expected)
    {
        Assert.Equal(expected, SaMetricBar.FormatValue(value, maximum));
    }

    [Theory]
    [InlineData(double.NaN, 100, 0)]
    [InlineData(0, 100, 0)]
    [InlineData(41, 100, 0.41)]
    [InlineData(-5, 100, 0)]
    [InlineData(250, 100, 1)]
    public void MetricBarFillIsClampedAndEmptyWhenUnknown(double value, double maximum, double expected)
    {
        Assert.Equal(expected, SaMetricBar.FillFraction(value, maximum), 6);
    }

    [Fact]
    public void UnknownIsTheDefaultReading()
    {
        var metadata = SaMetricBar.ValueProperty.GetMetadata(typeof(SaMetricBar));
        Assert.True(double.IsNaN((double)metadata.DefaultValue));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void SkeletonPulsesOnlyWhenRequestedAndAnimationsAreEnabled(bool requested, bool animationsEnabled, bool expected)
    {
        Assert.Equal(expected, SaSkeleton.ShouldPulse(requested, animationsEnabled));
    }

    [Theory]
    [InlineData(typeof(SaMetricBar), typeof(SaMetricKind), "MetricStates")]
    [InlineData(typeof(SaKeyValueRow), typeof(SaKeyValueOrientation), "OrientationStates")]
    [InlineData(typeof(SaListRow), typeof(SaListRowVariant), "VariantStates")]
    [InlineData(typeof(SaInlineNotice), typeof(SaNoticeSeverity), "SeverityStates")]
    public void EveryEnumValueHasADeclaredVisualState(Type control, Type vocabulary, string group)
    {
        var states = control.GetCustomAttributes<TemplateVisualStateAttribute>().Where(a => a.GroupName == group).Select(a => a.Name);
        Assert.Equal(Enum.GetNames(vocabulary).Order(StringComparer.Ordinal), states.Order(StringComparer.Ordinal));
    }
}
