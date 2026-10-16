using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Luna.Controls;

/// <summary>
/// 把一段文本渲染到 <see cref="TextBlock"/> 的 Inlines 中，并对匹配关键词的片段着色高亮。
/// 用法：<c>controls:HighlightText.Source="{Binding ...}" controls:HighlightText.Query="{Binding ...}"</c>。
/// </summary>
public static class HighlightText
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(HighlightText),
        new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached(
        "Query", typeof(string), typeof(HighlightText),
        new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty HighlightBackgroundProperty = DependencyProperty.RegisterAttached(
        "HighlightBackground", typeof(Brush), typeof(HighlightText),
        new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0xE9, 0xD9, 0x8C)), OnChanged));

    public static readonly DependencyProperty HighlightForegroundProperty = DependencyProperty.RegisterAttached(
        "HighlightForeground", typeof(Brush), typeof(HighlightText),
        new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x4A, 0x43, 0x3D)), OnChanged));

    public static string GetSource(DependencyObject d) => (string)d.GetValue(SourceProperty);
    public static void SetSource(DependencyObject d, string value) => d.SetValue(SourceProperty, value);

    public static string GetQuery(DependencyObject d) => (string)d.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject d, string value) => d.SetValue(QueryProperty, value);

    public static Brush GetHighlightBackground(DependencyObject d) => (Brush)d.GetValue(HighlightBackgroundProperty);
    public static void SetHighlightBackground(DependencyObject d, Brush value) => d.SetValue(HighlightBackgroundProperty, value);

    public static Brush GetHighlightForeground(DependencyObject d) => (Brush)d.GetValue(HighlightForegroundProperty);
    public static void SetHighlightForeground(DependencyObject d, Brush value) => d.SetValue(HighlightForegroundProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock textBlock)
            Build(textBlock);
    }

    private static void Build(TextBlock textBlock)
    {
        textBlock.Inlines.Clear();

        var source = GetSource(textBlock);
        var query = GetQuery(textBlock);
        if (string.IsNullOrEmpty(query))
        {
            textBlock.Inlines.Add(new Run(source));
            return;
        }

        var background = GetHighlightBackground(textBlock);
        var foreground = GetHighlightForeground(textBlock);

        var index = 0;
        while (index < source.Length)
        {
            var found = source.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                textBlock.Inlines.Add(new Run(source[index..]));
                break;
            }

            if (found > index)
                textBlock.Inlines.Add(new Run(source[index..found]));

            textBlock.Inlines.Add(new Run(source.Substring(found, query.Length))
            {
                Background = background,
                Foreground = foreground
            });

            index = found + query.Length;
        }
    }
}
