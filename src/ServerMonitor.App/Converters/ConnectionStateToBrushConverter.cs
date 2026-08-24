using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using ServerMonitor.Core.Enums;

namespace ServerMonitor.App.Converters;

public sealed class ConnectionStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var state = value is ServerConnectionState connectionState
            ? connectionState
            : ServerConnectionState.Error;
        var resourceKey = state switch
        {
            ServerConnectionState.Connected => "StatusHealthyBrush",
            ServerConnectionState.Connecting or ServerConnectionState.HostKeyUnknown => "StatusWarningBrush",
            ServerConnectionState.AuthenticationFailed
                or ServerConnectionState.HostKeyMismatch
                or ServerConnectionState.Error => "StatusCriticalBrush",
            ServerConnectionState.TimedOut or ServerConnectionState.Unreachable => "StatusOfflineBrush",
            _ => "StatusUnknownBrush"
        };
        return (Brush)Application.Current.Resources[resourceKey];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
