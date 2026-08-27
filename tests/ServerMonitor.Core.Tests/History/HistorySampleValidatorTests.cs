using ServerMonitor.Core.History;

namespace ServerMonitor.Core.Tests.History;

public sealed class HistorySampleValidatorTests
{
    [Fact]
    public void SanitizePercent_Null_ReturnsNull()
    {
        Assert.Null(HistorySampleValidator.SanitizePercent(null));
    }

    [Fact]
    public void SanitizePercent_Nan_ReturnsNull()
    {
        Assert.Null(HistorySampleValidator.SanitizePercent(double.NaN));
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SanitizePercent_Infinity_ReturnsNull(double value)
    {
        Assert.Null(HistorySampleValidator.SanitizePercent(value));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(150)]
    [InlineData(100000)]
    public void SanitizePercent_AbsurdlyOutOfRange_ReturnsNull(double value)
    {
        // Absurd readings must never corrupt a chart — dropped to null, not clamped/0.
        Assert.Null(HistorySampleValidator.SanitizePercent(value));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(100.2, 100)] // rounding noise within tolerance → clamped, not dropped
    [InlineData(-0.2, 0)]
    public void SanitizePercent_InRangeOrTolerance_ReturnsClampedValue(double input, double expected)
    {
        Assert.Equal(expected, HistorySampleValidator.SanitizePercent(input));
    }

    [Fact]
    public void SanitizePercent_ZeroIsAValidMeasurement_NotTreatedAsUnknown()
    {
        // unknown ≠ zero: a real 0 must survive as 0, and only genuine absence becomes null.
        Assert.Equal(0d, HistorySampleValidator.SanitizePercent(0));
        Assert.Null(HistorySampleValidator.SanitizePercent(null));
    }

    [Fact]
    public void IsValidTimestamp_DefaultOrPreEpoch_False()
    {
        Assert.False(HistorySampleValidator.IsValidTimestamp(default));
        Assert.False(HistorySampleValidator.IsValidTimestamp(new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void IsValidTimestamp_Realistic_True()
    {
        Assert.True(HistorySampleValidator.IsValidTimestamp(new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero)));
    }
}
