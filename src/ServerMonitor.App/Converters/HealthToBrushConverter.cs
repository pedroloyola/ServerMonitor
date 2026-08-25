using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Converters;

/// <summary>
/// Maps a <see cref="ServerHealth"/> to a semantic status brush for the card's status dot.
/// Health, not connection state, drives the dot colour: green/amber/red communicate the
/// operator's concern, while Offline uses the neutral-red offline token and Unknown stays
/// neutral. The brand accent (#1846E1) is deliberately never used here — it is reserved for
/// interaction and the micro progress bars.
/// </summary>
public sealed class HealthToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var health = value is ServerHealth serverHealth ? serverHealth : ServerHealth.Unknown;
        var resourceKey = health switch
        {
            ServerHealth.Healthy => "StatusHealthyBrush",
            ServerHealth.Warning => "StatusWarningBrush",
            ServerHealth.Critical => "StatusCriticalBrush",
            ServerHealth.Offline => "StatusOfflineBrush",
            _ => "StatusUnknownBrush"
        };
        return (Brush)Application.Current.Resources[resourceKey];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
