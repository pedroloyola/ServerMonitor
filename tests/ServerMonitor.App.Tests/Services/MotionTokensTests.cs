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

    /// <summary>
    /// UI.11 (Prism §4.1): the semantic aliases sit on the Fluent 83/167/250 scale; exits are shorter than entrances.
    /// UI.11.1 H01 moved the toggle (200) and the segmented selectors (300, their own alias) off the scale on purpose - see
    /// <see cref="Ui111Durations_AreTheHumanDecision"/>; every other alias stays on it.
    /// </summary>
    [Fact]
    public void Ui11SemanticAliases_StayOnTheFluentScale() => WithCulture("pt-PT", () =>
    {
        var tokens = MotionXaml();
        TimeSpan Time(string key) => MotionTokens.ParseTime(key, tokens[key]);

        Assert.Equal(Time(MotionTokens.FadeDuration), Time(MotionTokens.HoverDuration));
        Assert.Equal(Time(MotionTokens.NormalDuration), Time(MotionTokens.SelectDuration));
        Assert.Equal(Time(MotionTokens.FastDuration), Time(MotionTokens.EnterDuration));
        Assert.Equal(Time(MotionTokens.NormalDuration), Time(MotionTokens.EnterOffsetDuration));
        Assert.Equal(Time(MotionTokens.FadeDuration), Time(MotionTokens.ExitDuration));
        Assert.Equal(Time(MotionTokens.NormalDuration), Time(MotionTokens.ExpandDuration));
        Assert.Equal(Time(MotionTokens.FastDuration), Time(MotionTokens.CollapseDuration));
        Assert.True(Time(MotionTokens.ExitDuration) < Time(MotionTokens.EnterDuration));
        Assert.True(Time(MotionTokens.CollapseDuration) < Time(MotionTokens.ExpandDuration));
        Assert.True(Time(MotionTokens.HoverDuration) < Time(MotionTokens.ToggleDuration));
        Assert.True(Time(MotionTokens.ToggleDuration) < Time(MotionTokens.SelectDuration));
        Assert.Equal(new MotionKeySpline(1, 0, 1, 1), MotionTokens.ParseKeySpline(MotionTokens.ExitKeySpline, tokens[MotionTokens.ExitKeySpline]));
    });

    /// <summary>
    /// UI.11.1 H01 (human decision 2026-10-11, Prism UI11.1A §4.2-§4.4): exact values, so a change to one role can never drag
    /// another along. The five segmented selectors 300 (own alias); the sidebar/onboarding slide-select stays 250; toggle 200
    /// with its thumb colour swap at exactly half (100, own key - never the hover/fade 83); the drag settle 200. The base
    /// Fluent scale (Fade 83 / Fast 167 / Normal 250) is untouched.
    /// </summary>
    [Fact]
    public void Ui111Durations_AreTheHumanDecision() => WithCulture("pt-PT", () =>
    {
        var tokens = MotionXaml();
        double Ms(string key) => MotionTokens.ParseTime(key, tokens[key]).TotalMilliseconds;

        Assert.Equal(300, Ms(MotionTokens.SegmentSelectDuration));
        Assert.Equal(250, Ms(MotionTokens.SelectDuration));
        Assert.Equal(200, Ms(MotionTokens.ToggleDuration));
        Assert.Equal(100, Ms(MotionTokens.ToggleSwapTime));
        Assert.Equal(Ms(MotionTokens.ToggleDuration) / 2, Ms(MotionTokens.ToggleSwapTime));
        Assert.Equal(200, Ms(MotionTokens.SettleDuration));
        Assert.Equal(83, Ms(MotionTokens.HoverDuration));
        Assert.Equal(83, Ms(MotionTokens.FadeDuration));
        Assert.Equal(167, Ms(MotionTokens.FastDuration));
        Assert.Equal(250, Ms(MotionTokens.NormalDuration));

        // Hierarchy (Prism §4.3): feedback < local state < context < longest travel; the settle ends a gesture faster than
        // a full selection.
        Assert.True(Ms(MotionTokens.HoverDuration) < Ms(MotionTokens.ToggleSwapTime));
        Assert.True(Ms(MotionTokens.ToggleSwapTime) < Ms(MotionTokens.ToggleDuration));
        Assert.True(Ms(MotionTokens.ToggleDuration) < Ms(MotionTokens.SelectDuration));
        Assert.True(Ms(MotionTokens.SelectDuration) < Ms(MotionTokens.SegmentSelectDuration));
        Assert.True(Ms(MotionTokens.SettleDuration) < Ms(MotionTokens.SegmentSelectDuration));
        Assert.True(MotionTokens.TimeKeys.All(key => key == MotionTokens.SlowDuration || key.StartsWith("SaMotionFocus", StringComparison.Ordinal)
            || Ms(key) <= 300), "no motion pattern exceeds 300 ms");
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
