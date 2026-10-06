using System.Globalization;
using System.Windows.Data;
using Luna.Services;

namespace Luna.Converters;

/// <summary>把字节数格式化为人类可读的大小（B / KB / MB / GB）。</summary>
public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var bytes = value switch
        {
            long l => l,
            int i => i,
            _ => 0L,
        };
        return AttachmentHelper.FormatSize(bytes);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
