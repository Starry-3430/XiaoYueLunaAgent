using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Luna.Services;

/// <summary>
/// 运行时主题（字体）应用。
/// 通过遍历可视树 + 监听 Loaded 事件，把用户选择的字体应用到所有窗口与文本组件；
/// 代码/等宽字体（如 Cascadia Mono）会被跳过以保持代码块样式。
/// </summary>
public static class ThemeService
{
    // 代码块字体：优先系统安装的 Cascadia Mono，缺失时回退 Consolas。
    private static readonly FontFamily CodeFontFamily = new("Cascadia Mono, Consolas");

    private static FontFamily? _currentFont;
    private static bool _initialized;

    /// <summary>当前生效的字体；为空表示使用系统/内置默认字体。</summary>
    public static FontFamily? CurrentFont => _currentFont;

    /// <summary>正文 Typeface（未设置自定义字体时为 null）。</summary>
    public static Typeface? BaseTypeface => _currentFont is null
        ? null
        : new Typeface(_currentFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>等宽/代码 Typeface（固定为内置 Cascadia Mono）。</summary>
    public static Typeface CodeTypeface { get; } =
        new(CodeFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>注册全局 Loaded 监听，应在窗口创建前调用。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnElementLoaded));
    }

    /// <summary>把字体应用到所有已存在窗口，并设为其后新组件的默认字体。</summary>
    public static void ApplyFont(string fontFamily)
    {
        Initialize();

        _currentFont = string.IsNullOrWhiteSpace(fontFamily) ? null : new FontFamily(fontFamily);
        if (_currentFont is null) return;

        foreach (Window window in Application.Current.Windows)
            ApplyToTree(window);
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (_currentFont is null) return;
        if (sender is DependencyObject element)
            ApplyToElement(element);
    }

    private static void ApplyToTree(DependencyObject root)
    {
        ApplyToElement(root);

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            ApplyToTree(VisualTreeHelper.GetChild(root, i));
    }

    private static void ApplyToElement(DependencyObject element)
    {
        switch (element)
        {
            case Control control when !IsMonospace(control.FontFamily):
                control.FontFamily = _currentFont!;
                break;
            case TextBlock textBlock when !IsMonospace(textBlock.FontFamily):
                textBlock.FontFamily = _currentFont!;
                break;
            case TextElement textElement when !IsMonospace(textElement.FontFamily):
                textElement.FontFamily = _currentFont!;
                break;
        }
    }

    private static bool IsMonospace(FontFamily? family)
    {
        var source = family?.Source;
        if (string.IsNullOrEmpty(source)) return false;

        return source.Contains("Cascadia", StringComparison.OrdinalIgnoreCase) ||
               source.Contains("Consolas", StringComparison.OrdinalIgnoreCase) ||
               source.Contains("Courier", StringComparison.OrdinalIgnoreCase) ||
               source.Contains("Mono", StringComparison.OrdinalIgnoreCase);
    }
}
