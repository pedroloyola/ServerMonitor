using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Converters;

/// <summary>
/// Selects the <see cref="Style"/> for a state/health text label from its severity (M-01): a Negative
/// label is SemiBold and critical-coloured, Warning is amber, Positive/Neutral use the primary text
/// colour. It returns a Style whose <c>Foreground</c> setter is a <c>{ThemeResource}</c>, so the colour is
/// theme-aware and re-resolves for Light and Dark (H-03) — a "Saudável"/"Parado" label is no longer the
/// frozen dark-theme white on a light background. State is still text, so colour is never the sole signal
/// (§53).
/// </summary>
public sealed class WorkloadSeverityToTextStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var severity = value is WorkloadSeverity workloadSeverity ? workloadSeverity : WorkloadSeverity.Neutral;
        var key = severity switch
        {
            WorkloadSeverity.Negative => "WorkloadStateTextNegativeStyle",
            WorkloadSeverity.Warning => "WorkloadStateTextWarningStyle",
            _ => "WorkloadStateTextNeutralStyle"
        };
        return (Style)Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
