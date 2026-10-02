using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using ServerMonitor.App.Controls.Primitives;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Converters;

/// <summary>
/// UI.3 Workloads rows: severity -> the dot of <c>SaStatusDotOnlyStyle</c>. D-UI3-2: workload failures (and the
/// transient warnings) use ATTENTION, never the red error colour, which stays reserved for collection errors / no
/// connection. Presentation only - <see cref="WorkloadSeverity"/> in the domain is unchanged.
/// </summary>
public sealed class WorkloadSeverityToStatusKindConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => For(value as WorkloadSeverity?);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    public static SaStatusKind For(WorkloadSeverity? severity) => severity switch
    {
        WorkloadSeverity.Positive => SaStatusKind.Healthy,
        WorkloadSeverity.Negative or WorkloadSeverity.Warning => SaStatusKind.Attention,
        _ => SaStatusKind.Unknown
    };
}

/// <summary>
/// UI.3 Workloads rows: severity -> Sa text style for the two right-hand state lines (Figma 112:2666: a problem row has
/// BOTH lines in the attention text colour). ConverterParameter "Detail" selects the 11 Regular second line, anything
/// else the 12 Medium first line. The styles' Foreground is a ThemeResource, so Light/Dark/HC re-resolve; the state is
/// always text, never colour alone.
/// </summary>
public sealed class WorkloadSeverityToSaTextStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        (Style)Application.Current.Resources[KeyFor(value as WorkloadSeverity?, parameter as string)];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    public static string KeyFor(WorkloadSeverity? severity, string? line)
    {
        var attention = severity is WorkloadSeverity.Negative or WorkloadSeverity.Warning;
        var detail = string.Equals(line, "Detail", StringComparison.Ordinal);
        return (detail, attention) switch
        {
            (false, false) => "SaWorkloadStateTextStyle",
            (false, true) => "SaWorkloadStateAttentionTextStyle",
            (true, false) => "SaWorkloadDetailTextStyle",
            (true, true) => "SaWorkloadDetailAttentionTextStyle"
        };
    }
}
