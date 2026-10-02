using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Converters;

/// <summary>
/// UI.4: picks one of five Styles by the engine's <see cref="ServerHealth"/> (health-bar segment, priority title and
/// bar, attention metric cell). It returns a STYLE whose brushes are <c>{ThemeResource}</c>, never a Brush resolved here:
/// a converter that resolves a themed brush once keeps the Dark variant in Light (M11 H-03). A missing style falls back
/// to <see cref="UnknownStyle"/> (never to the healthy look).
/// </summary>
public sealed class ServerHealthToStyleConverter : IValueConverter
{
    public Style? HealthyStyle { get; set; }

    public Style? WarningStyle { get; set; }

    public Style? CriticalStyle { get; set; }

    public Style? OfflineStyle { get; set; }

    public Style? UnknownStyle { get; set; }

    public Style? Select(ServerHealth health) => health switch
    {
        ServerHealth.Healthy => HealthyStyle,
        ServerHealth.Warning => WarningStyle,
        ServerHealth.Critical => CriticalStyle,
        ServerHealth.Offline => OfflineStyle,
        _ => null
    } ?? UnknownStyle;

    public object? Convert(object value, Type targetType, object parameter, string language) =>
        Select(value is ServerHealth health ? health : ServerHealth.Unknown);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
