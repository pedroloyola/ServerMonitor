using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using ServerMonitor.App.ViewModels;

namespace ServerMonitor.App.Converters;

/// <summary>
/// Selects the status-dot <see cref="Style"/> for a <see cref="WorkloadSeverity"/> (§52). It returns a
/// Style, never a resolved <c>Brush</c>: the Style's <c>Fill</c> setter is a <c>{ThemeResource}</c>, so the
/// dot colour re-resolves automatically for Light and Dark (H-03). Resolving a brush directly from
/// <c>Application.Current.Resources</c> would freeze it to one theme's variant — that was the bug.
/// The brand accent (#1846E1) is never used here; colour is reserved for health, and state is also
/// carried as text (§53).
/// </summary>
public sealed class WorkloadSeverityToDotStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var severity = value is WorkloadSeverity workloadSeverity ? workloadSeverity : WorkloadSeverity.Neutral;
        var key = severity switch
        {
            WorkloadSeverity.Positive => "WorkloadDotPositiveStyle",
            WorkloadSeverity.Warning => "WorkloadDotWarningStyle",
            WorkloadSeverity.Negative => "WorkloadDotNegativeStyle",
            _ => "WorkloadDotNeutralStyle"
        };
        return (Style)Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
