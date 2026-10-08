using Microsoft.UI.Xaml;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Converters;

/// <summary>
/// UI.8: the Compact row's x:Bind functions (Prism R-5). They return keyed STYLES / primitive tones whose brushes are
/// <c>{ThemeResource}</c> - never a brush resolved here - so a live theme change repaints (M11 H-03). The key choice is
/// pure (<see cref="ValueStyleKey"/>, <see cref="StatusStyleKey"/>, <see cref="Tone"/>) and unit-tested.
/// </summary>
public static class CompactRowStyles
{
    /// <summary>Unknown / Offline "—" and a stale (retained) value are muted; otherwise neutral, attention or critical.</summary>
    public static string ValueStyleKey(bool known, ServerHealth severity, bool stale) =>
        !known || stale ? "SaCompactMetricValueMutedTextStyle"
        : severity switch
        {
            ServerHealth.Warning => "SaCompactMetricValueAttentionTextStyle",
            ServerHealth.Critical => "SaCompactMetricValueCriticalTextStyle",
            _ => "SaCompactMetricValueTextStyle"
        };

    /// <summary>Healthy and no-data labels stay secondary (Figma); the other states take their colour.</summary>
    public static string StatusStyleKey(ServerHealth health) => health switch
    {
        ServerHealth.Warning => "SaCompactStatusAttentionTextStyle",
        ServerHealth.Critical => "SaCompactStatusCriticalTextStyle",
        ServerHealth.Offline => "SaCompactStatusOfflineTextStyle",
        _ => "SaCompactStatusTextStyle"
    };

    /// <summary>The bar uses the SAME colour as the value (Figma): neutral, attention or critical; never for an unknown.</summary>
    public static SaMetricTone Tone(bool known, ServerHealth severity) =>
        !known ? SaMetricTone.Neutral
        : severity switch
        {
            ServerHealth.Warning => SaMetricTone.Attention,
            ServerHealth.Critical => SaMetricTone.Critical,
            _ => SaMetricTone.Neutral
        };

    /// <summary>The bar's reading: <see cref="double.NaN"/> (no fill) when unknown - never 0 standing in for "no data".</summary>
    public static double BarValue(bool known, double value) => known ? value : double.NaN;

    public static Style? Value(bool known, ServerHealth severity, bool stale) => Find(ValueStyleKey(known, severity, stale));

    public static Style? Status(ServerHealth health) => Find(StatusStyleKey(health));

    public static SaStatusKind StatusKind(ServerHealth health) => ServerHealthToStatusKindConverter.Map(health);

    private static Style? Find(string key) =>
        Application.Current?.Resources is { } resources && resources.TryGetValue(key, out var style) ? style as Style : null;
}
