using System.Globalization;
using System.Windows.Data;
using Luna.Services;

namespace Luna.Converters;

/// <summary>
/// 把附件的字节数与 Token 估算组合为元信息文本：<c>(23 KB, ≈ 8,400 tokens)</c>。
/// 单独渲染，确保文件名被省略时该部分仍完整显示。
/// </summary>
public class AttachmentMetaConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var size = values.Length > 0 ? ToLong(values[0]) : 0L;
        var tokens = values.Length > 1 ? (int)ToLong(values[1]) : 0;

        var sizeText = AttachmentHelper.FormatSize(size);
        return tokens > 0
            ? $"({sizeText}, ≈ {tokens:N0} tokens)"
            : $"({sizeText})";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static long ToLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        _ => 0L,
    };
}
