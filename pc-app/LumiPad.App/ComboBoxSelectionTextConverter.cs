using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace LumiPad.App;

public sealed class ComboBoxSelectionTextConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (value is null)
            return string.Empty;

        if (value is ComboBoxItem item)
            return item.Content?.ToString() ?? string.Empty;

        if (value is ContentControl contentControl)
            return contentControl.Content?.ToString() ?? string.Empty;

        return value.ToString() ?? string.Empty;
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
