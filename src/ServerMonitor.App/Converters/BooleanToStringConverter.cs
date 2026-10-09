using Microsoft.UI.Xaml.Data;

namespace ServerMonitor.App.Converters;

/// <summary>
/// One-way: picks <see cref="TrueValue"/> or <see cref="FalseValue"/> for a bound boolean. UI.10 F12: the Workloads
/// "no results" panel shows the tick for the positive filter case (B) and the search glass otherwise (A/C), with the
/// icon path data declared in XAML so the view model never touches resources.
/// </summary>
public sealed class BooleanToStringConverter : IValueConverter
{
    public string TrueValue { get; set; } = string.Empty;

    public string FalseValue { get; set; } = string.Empty;

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? TrueValue : FalseValue;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
