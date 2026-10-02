using Microsoft.UI.Xaml.Data;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Converters;

/// <summary>
/// UI.4: maps the engine's <see cref="ServerHealth"/> onto the UI.2 <see cref="SaStatusKind"/> that
/// <see cref="SaStatusIndicator"/> renders (text + colour, never colour alone). Critical is the "error" kind — the
/// additional real state the Figma frames do not draw — and an unknown value is never shown as healthy.
/// </summary>
public sealed class ServerHealthToStatusKindConverter : IValueConverter
{
    public static SaStatusKind Map(ServerHealth health) => health switch
    {
        ServerHealth.Healthy => SaStatusKind.Healthy,
        ServerHealth.Warning => SaStatusKind.Attention,
        ServerHealth.Critical => SaStatusKind.Error,
        ServerHealth.Offline => SaStatusKind.Offline,
        _ => SaStatusKind.Unknown
    };

    public object Convert(object value, Type targetType, object parameter, string language) =>
        Map(value is ServerHealth health ? health : ServerHealth.Unknown);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
