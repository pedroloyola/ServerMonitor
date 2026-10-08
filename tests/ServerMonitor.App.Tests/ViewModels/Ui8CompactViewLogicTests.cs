using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.Converters;
using ServerMonitor.App.Services;
using ServerMonitor.App.Tests.Fakes;
using ServerMonitor.App.ViewModels;
using ServerMonitor.App.Views;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Tests.ViewModels;

/// <summary>
/// UI.8 8C: the pure decisions behind the Compact XAML - the title strip's measured layout (Prism R-3), the row's
/// severity styles / bar tone (R-5), the 3 px minimum bar (R-4) and the state block's copy and actions (R-8). No window.
/// </summary>
public sealed class Ui8CompactViewLogicTests
{
    // ---- R-3 title strip: measured, priority captions > Expandir > mark > wordmark ----

    private const double Mark = 23;
    private const double Wordmark = 90;
    private const double ButtonWithText = 100;

    [Theory]
    [InlineData(274, true, true, true)]     // = 23 + 12 + 90 + 12 + 100 + 37 spare: text + mark + wordmark
    [InlineData(237, true, true, true)]     // exactly the threshold
    [InlineData(236, false, true, true)]    // one DIP under: icon-only button, everything else stays
    [InlineData(123, false, true, true)]    // icon 36: mark 23 + 12 + 12 + 36 + wordmark 40
    [InlineData(122, false, true, false)]   // the wordmark goes first (below its minimum)
    [InlineData(71, false, true, false)]    // mark + 12 + icon
    [InlineData(70, false, false, false)]   // the mark goes before the button
    [InlineData(36, false, false, false)]   // the button itself is never hidden
    public void TheTitleStrip_YieldsInTheDecidedOrder(double available, bool text, bool mark, bool wordmark)
    {
        var decision = CompactTitleLayout.Decide(available, Mark, Wordmark, ButtonWithText);

        Assert.Equal(new CompactTitleLayout.Decision(text, mark, wordmark), decision);
    }

    [Fact]
    public void TheThreshold_FollowsTheMeasuredText_NotAFixedPixel()
    {
        // A longer translation of "Expandir" (or a larger text scale) moves the threshold with it.
        Assert.True(CompactTitleLayout.Decide(240, Mark, Wordmark, 100).ShowExpandText);
        Assert.False(CompactTitleLayout.Decide(240, Mark, Wordmark, 110).ShowExpandText);
    }

    // ---- R-5 row styles / bar tone ----

    [Theory]
    [InlineData(true, ServerHealth.Healthy, false, "SaCompactMetricValueTextStyle")]
    [InlineData(true, ServerHealth.Warning, false, "SaCompactMetricValueAttentionTextStyle")]
    [InlineData(true, ServerHealth.Critical, false, "SaCompactMetricValueCriticalTextStyle")]
    [InlineData(false, ServerHealth.Healthy, false, "SaCompactMetricValueMutedTextStyle")]
    [InlineData(false, ServerHealth.Critical, false, "SaCompactMetricValueMutedTextStyle")]
    [InlineData(true, ServerHealth.Critical, true, "SaCompactMetricValueMutedTextStyle")]
    public void TheValueStyle_IsNeutralUnlessTheEngineLimitIsCrossed_AndMutedWhenUnknownOrStale(bool known, ServerHealth severity, bool stale, string key) =>
        Assert.Equal(key, CompactRowStyles.ValueStyleKey(known, severity, stale));

    [Theory]
    [InlineData(ServerHealth.Healthy, "SaCompactStatusTextStyle")]
    [InlineData(ServerHealth.Unknown, "SaCompactStatusTextStyle")]
    [InlineData(ServerHealth.Warning, "SaCompactStatusAttentionTextStyle")]
    [InlineData(ServerHealth.Critical, "SaCompactStatusCriticalTextStyle")]
    [InlineData(ServerHealth.Offline, "SaCompactStatusOfflineTextStyle")]
    [InlineData((ServerHealth)42, "SaCompactStatusTextStyle")]
    public void TheStatusLabelStyle_IsSecondaryForHealthyAndNoData_AndTheStateColourOtherwise(ServerHealth health, string key) =>
        Assert.Equal(key, CompactRowStyles.StatusStyleKey(health));

    [Theory]
    [InlineData(true, ServerHealth.Healthy, SaMetricTone.Neutral)]
    [InlineData(true, ServerHealth.Warning, SaMetricTone.Attention)]
    [InlineData(true, ServerHealth.Critical, SaMetricTone.Critical)]
    [InlineData(false, ServerHealth.Critical, SaMetricTone.Neutral)]
    public void TheBarTone_IsTheValueColour(bool known, ServerHealth severity, SaMetricTone tone) =>
        Assert.Equal(tone, CompactRowStyles.Tone(known, severity));

    [Fact]
    public void AnUnknownMetric_FeedsTheBarNaN_NeverZero()
    {
        Assert.True(double.IsNaN(CompactRowStyles.BarValue(false, 0)));
        Assert.Equal(42, CompactRowStyles.BarValue(true, 42));
        Assert.Equal(ServerMonitor.App.Controls.Primitives.SaStatusKind.Offline, CompactRowStyles.StatusKind(ServerHealth.Offline));
    }

    // ---- R-4 bar ----

    [Theory]
    [InlineData(double.NaN, 112, 0)]
    [InlineData(0, 112, 0)]
    [InlineData(-5, 112, 0)]
    [InlineData(1, 112, 3)]       // 1.12 px -> the 3 px minimum
    [InlineData(24, 112, 26.88)]  // Figma 24 % -> 27
    [InlineData(92, 112, 103.04)] // Figma 92 % -> 103
    [InlineData(150, 112, 112)]   // clamped to the track
    [InlineData(50, 0, 0)]        // not laid out yet
    public void TheBarFill_IsProportional_WithA3PxMinimum_AndNothingForUnknownOrZero(double value, double track, double expected) =>
        Assert.Equal(expected, SaCompactMetricBar.FillWidth(value, track), 6);

    // ---- R-8 state block ----

    [Fact]
    public async Task TheStateBlock_UsesTheDecidedCopy_AndOnlyRealActions()
    {
        var localization = new ResWLocalizationService("pt-PT");

        var empty = Ui4TestKit.Create(new Ui4TestKit.Fleet(), localization: localization);
        await empty.Dashboard.LoadAsync();
        using (var compact = new CompactPresentationViewModel(empty.Dashboard, localization))
        {
            Assert.Equal((true, false, true, false, true, false, false), Flags(compact));
            Assert.Equal("Ainda não tens servidores", compact.StateTitle);
            Assert.Equal("Adiciona o primeiro para começar a monitorizar.", compact.StateBody);
        }

        var hidden = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("h", ServerHealth.Healthy, 1, 2, 3, hidden: true), localization: localization);
        await hidden.Dashboard.LoadAsync();
        using (var compact = new CompactPresentationViewModel(hidden.Dashboard, localization))
        {
            Assert.Equal((true, false, true, false, false, true, false), Flags(compact));
            Assert.Equal("Os teus servidores estão ocultos.", compact.StateTitle);
        }

        var unavailable = Ui4TestKit.Create(new Ui4TestKit.Fleet(), localization: localization);
        unavailable.Servers.LoadStatus = Core.Interfaces.ServerLoadStatus.Unavailable;
        await unavailable.Dashboard.LoadAsync();
        using (var compact = new CompactPresentationViewModel(unavailable.Dashboard, localization))
        {
            Assert.Equal((true, false, false, true, false, false, false), Flags(compact));
            Assert.Equal("Configuração indisponível", compact.StateTitle);
            Assert.Equal(localization.GetString("ConfigurationUnavailableNotice.Message"), compact.StateBody);
        }

        var loading = Ui4TestKit.Create(new Ui4TestKit.Fleet(), localization: localization);
        using (var compact = new CompactPresentationViewModel(loading.Dashboard, localization))
        {
            Assert.Equal((true, true, false, false, false, false, false), Flags(compact));
            Assert.Equal("A carregar os teus servidores…", compact.StateTitle);
            Assert.False(compact.HasStateBody);
        }

        var list = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3), localization: localization);
        await list.Dashboard.LoadAsync();
        using (var compact = new CompactPresentationViewModel(list.Dashboard, localization))
        {
            Assert.Equal((false, false, false, false, false, false, true), Flags(compact));
            Assert.Equal(string.Empty, compact.StateTitle);
        }
    }

    [Fact]
    public async Task AStateChange_AnnouncesEveryStateProperty()
    {
        var harness = Ui4TestKit.Create(new Ui4TestKit.Fleet().Add("web", ServerHealth.Healthy, 1, 2, 3));
        using var compact = new CompactPresentationViewModel(harness.Dashboard, harness.Localization);
        var raised = new List<string>();
        compact.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        await harness.Dashboard.LoadAsync(); // Loading -> List

        Assert.Superset(
            new HashSet<string>
            {
                nameof(compact.BodyState), nameof(compact.ShowsList), nameof(compact.ShowsStateBlock), nameof(compact.ShowsLoading),
                nameof(compact.ShowsServerIcon), nameof(compact.ShowsAlertIcon), nameof(compact.ShowsAddAction), nameof(compact.ShowsHiddenAction),
                nameof(compact.StateTitle), nameof(compact.StateBody), nameof(compact.HasStateBody), nameof(compact.ShowsSummary)
            },
            raised.ToHashSet());
    }

    // (state block, loading, server icon, alert icon, add, manage hidden, list)
    private static (bool, bool, bool, bool, bool, bool, bool) Flags(CompactPresentationViewModel c) =>
        (c.ShowsStateBlock, c.ShowsLoading, c.ShowsServerIcon, c.ShowsAlertIcon, c.ShowsAddAction, c.ShowsHiddenAction, c.ShowsList);
}
