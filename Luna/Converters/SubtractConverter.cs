using System.Globalization;
using System.Windows.Data;

namespace Luna.Converters;

public class SubtractConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double d && double.TryParse(parameter?.ToString(), out var subtract))
            return Math.Max(0, d - subtract);
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}