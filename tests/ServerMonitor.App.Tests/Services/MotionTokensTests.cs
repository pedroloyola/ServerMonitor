using System.Globalization;
using ServerMonitor.App.Services.Motion;
using ServerMonitor.App.Tests.Architecture;

namespace ServerMonitor.App.Tests.Services;

/// <summary>
/// T-9 (UI.2 S3, F-4). Every motion token in Styles/Tokens/Motion.xaml parses - under a pt-PT current culture,
/// whose decimal separator is a comma - and malformed values fail closed with the key in the message.
/// </summary>
public sealed class MotionTokensTests
{
    private static IReadOnlyDictionary<string, string> MotionXaml() =>
        AppSourceTree.LoadXaml("Styles/Tokens/Motion.xaml").Root!.Elements()
            .Where(e => e.Name.LocalName == "String")
            .ToDictionary(e => (string)e.Attribute(AppSourceTree.Xaml + "Key")!, e => e.Value, StringComparer.Ordinal);

    [Fact]
    public void KeyListsMatchMotionXamlExactly()
    {
        var keys = MotionXaml().Keys.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(keys.Count, MotionTokens.TimeKeys.Count + MotionTokens.KeySplineKeys.Count);
        Assert.Subset(keys, MotionTokens.TimeKeys.Concat(MotionTokens.KeySplineKeys).ToHashSet(StringComparer.Ordinal));
        Assert.Superset(keys, MotionTokens.TimeKeys.Concat(MotionTokens.KeySplineKeys).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryMotionTokenParsesUnderPortugueseCulture() => WithCulture("pt-PT", () =>
    {
        var tokens = MotionXaml();
        foreach (var key in MotionTokens.TimeKeys)
        {
            Assert.True(MotionTokens.ParseTime(key, tokens[key]) > TimeSpan.Zero, key);
        }

        foreach (var key in MotionTokens.KeySplineKeys)
        {
            _ = MotionTokens.ParseKeySpline(key, tokens[key]);
        }
    });

    [Fact]
    public void KnownValuesParseExactly() => WithCulture("pt-PT", () =>
    {
        Assert.Equal(TimeSpan.FromMilliseconds(180), MotionTokens.ParseTime(MotionTokens.FocusPeakTime, "0:0:0.180"));
        Assert.Equal(TimeSpan.FromSeconds(2), MotionTokens.ParseTime(MotionTokens.FocusReducedHoldTime, "0:0:2"));
        Assert.Equal(new MotionKeySpline(0.55, 0.55, 0, 1), MotionTokens.ParseKeySpline(MotionTokens.PointToPointKeySpline, "0.55,0.55,0,1"));
    });

    [Fact]
    public void KeySplineTextIsInvariantAndRoundTrips() => WithCulture("pt-PT", () =>
    {
        var spline = MotionTokens.ParseKeySpline(MotionTokens.PointToPointKeySpline, "0.55,0.55,0,1");

        Assert.Equal("0.55,0.55,0,1", spline.ToString());
        Assert.Equal(spline, MotionTokens.ParseKeySpline(MotionTokens.PointToPointKeySpline, spline.ToString()));
    });

    [Theory]
    [InlineData("0:0:0,180")]   // pt-PT decimal comma: must never be accepted
    [InlineData("180ms")]
    [InlineData("0.180")]
    [InlineData("0:0:0")]       // zero is not a duration
    [InlineData("-0:0:1")]
    [InlineData("0:0:0.180\n")] // Cortex F-8: a trailing newline is not a valid token
    [InlineData("")]
    [InlineData(null)]
    public void MalformedTimesFailClosedNamingTheKey(string? value) => WithCulture("pt-PT", () =>
    {
        var error = Assert.Throws<InvalidOperationException>(() => MotionTokens.ParseTime(MotionTokens.FastDuration, value));
        Assert.Contains(MotionTokens.FastDuration, error.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void ANonStringTimeFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => MotionTokens.ParseTime(MotionTokens.FastDuration, 0.167));
    }

    [Theory]
    [InlineData("0,0,1")]               // three values
    [InlineData("0,0,1,1,1")]           // five values
    [InlineData("0.55,0.55,0,1.2")]     // y > 1
    [InlineData("1.5,0,0,1")]           // x > 1
    [InlineData("-0.1,0,0,1")]          // negative
    [InlineData("0;0;1;1")]
    [InlineData(null)]
    public void MalformedKeySplinesFailClosedNamingTheKey(string? value) => WithCulture("pt-PT", () =>
    {
        var error = Assert.Throws<InvalidOperationException>(() => MotionTokens.ParseKeySpline(MotionTokens.DirectKeySpline, value));
        Assert.Contains(MotionTokens.DirectKeySpline, error.Message, StringComparison.Ordinal);
    });

    private static void WithCulture(string culture, Action test)
    {
        var (current, currentUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);
            test();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (current, currentUi);
        }
    }
}
