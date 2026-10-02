using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ServerMonitor.App.Converters;

/// <summary>
/// Two-way bridge between an integer selection index on a view model and one RadioButton of a segmented control
/// (UI.3 History range, Workloads filter): IsChecked is true when the bound index equals ConverterParameter; checking
/// the button writes that index back. Unchecking never writes (the newly checked sibling does), so the index can never
/// be cleared by the group's own bookkeeping.
/// </summary>
public sealed class IndexToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int index && TryParse(parameter, out var wanted) && index == wanted;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is true && TryParse(parameter, out var wanted) ? wanted : DependencyProperty.UnsetValue;

    internal static bool TryParse(object? parameter, out int index) =>
        int.TryParse(parameter?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out index);
}
