using System.Xml.Linq;
using ServerMonitor.App.Services.Motion;

namespace ServerMonitor.App.Tests.Architecture;

/// <summary>
/// UI.11.1 H01 (human decision 2026-10-11; Prism UI11.1A §4.2/§4.4 "pin every consumer to its alias"). The slide-select
/// duration is chosen PER HOST: the five segmented selectors use SaMotionSegmentSelectDuration (300), the sidebar keeps the
/// shared SaMotionSelectDuration (250), and so does the onboarding dot. The rule is behavioural (the controller resolves
/// the host key through it); the XAML inventory below complements it - it never proves the runtime timing (UI.11.1D QA).
/// </summary>
public sealed class Ui111DurationContractTests
{
    private static readonly XNamespace Primitives = "using:ServerMonitor.App.Controls.Primitives";
    private static readonly XName IndicatorBrush = Primitives + "SaSlidingSelection.IndicatorBrush";
    private static readonly XName DurationKey = Primitives + "SaSlidingSelection.DurationKey";

    private static readonly string[] SegmentedHostFiles =
        ["Views/SettingsPage.xaml", "Views/HistoryPage.xaml", "Views/WorkloadsPage.xaml", "Controls/ServerFormControl.xaml"];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DurationKey_Unset_IsTheSharedSelectDuration(string? hostKey) =>
        Assert.Equal(MotionTokens.SelectDuration, SelectionIndicatorRules.DurationKey(hostKey));

    [Fact]
    public void DurationKey_SegmentHost_UsesItsOwnKey() =>
        Assert.Equal(MotionTokens.SegmentSelectDuration, SelectionIndicatorRules.DurationKey(MotionTokens.SegmentSelectDuration));

    [Theory]
    [InlineData("SaMotionSegmentSelectDurationX")]
    [InlineData("saMotionSegmentSelectDuration")]
    [InlineData(MotionTokens.PointToPointKeySpline)]
    public void DurationKey_NotATimeToken_FailsClosed(string hostKey)
    {
        var error = Assert.Throws<InvalidOperationException>(() => SelectionIndicatorRules.DurationKey(hostKey));
        Assert.Contains(hostKey, error.Message, StringComparison.Ordinal);
    }

    /// <summary>Exactly the five production segmented selectors opt into 300; nothing else in production does.</summary>
    [Fact]
    public void ExactlyTheFiveSegmentedHosts_UseTheSegmentDuration()
    {
        var production = AppSourceTree.Files(".xaml").Where(f => !AppSourceTree.IsUnderStyles(f) && !f.StartsWith("Qa/", StringComparison.Ordinal));
        var withKey = production
            .SelectMany(file => AppSourceTree.LoadXaml(file).Descendants().Where(e => e.Attribute(DurationKey) is not null).Select(host => (file, host)))
            .ToList();

        Assert.Equal(5, withKey.Count);
        Assert.All(withKey, entry =>
        {
            Assert.Contains(entry.file, SegmentedHostFiles);
            Assert.NotNull(entry.host.Attribute(IndicatorBrush));
            Assert.Equal(MotionTokens.SegmentSelectDuration, (string?)entry.host.Attribute(DurationKey));
        });
        Assert.Equal(2, withKey.Count(entry => entry.file == "Controls/ServerFormControl.xaml"));
    }

    /// <summary>The sidebar (F07) stays at 250: its host never sets a key, so the rule resolves the shared SelectDuration.</summary>
    [Fact]
    public void TheSidebar_KeepsTheSharedSelectDuration()
    {
        var host = Assert.Single(AppSourceTree.LoadXaml("Controls/SaSidebar.xaml").Descendants().Where(e => e.Attribute(IndicatorBrush) is not null));

        Assert.Null(host.Attribute(DurationKey));
        Assert.Equal(MotionTokens.SelectDuration, SelectionIndicatorRules.DurationKey((string?)host.Attribute(DurationKey)));
    }

    /// <summary>Every DurationKey anywhere (gallery included) resolves - a typo would fail closed at runtime.</summary>
    [Fact]
    public void EveryDurationKeyInXaml_IsATimeToken()
    {
        var keys = AppSourceTree.Files(".xaml")
            .SelectMany(file => AppSourceTree.LoadXaml(file).Descendants().Select(e => (string?)e.Attribute(DurationKey)).OfType<string>())
            .ToList();

        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.Equal(key, SelectionIndicatorRules.DurationKey(key)));
    }

    /// <summary>The onboarding dot (progress, Prism §4.2) keeps the 250 slide-select: it reads SelectDuration, never the segment key.</summary>
    [Fact]
    public void TheOnboardingDot_KeepsTheSharedSelectDuration()
    {
        var code = AppSourceTree.CodeWithoutComments("Controls/OnboardingView.xaml.cs");

        Assert.Contains("MotionTokens.SelectDuration", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SegmentSelectDuration", code, StringComparison.Ordinal);
    }
}
