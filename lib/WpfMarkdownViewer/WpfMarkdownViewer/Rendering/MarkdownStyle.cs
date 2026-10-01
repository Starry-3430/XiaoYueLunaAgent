using System.Windows;
using System.Windows.Media;

namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// The public, strongly-typed appearance configuration for the renderer (M2-1): fonts, sizes, weights,
/// line heights, margins/padding, list indent, and colors — plus the paired TextMate code theme. Ships
/// coordinated <see cref="Light"/> / <see cref="Dark"/> presets and is settable via the control's
/// <c>Style</c> dependency property (code or XAML), runtime-swappable. Records are immutable; use
/// <c>with</c> to derive a tweaked style.
/// </summary>
public sealed record MarkdownStyle
{
    // --- Fonts & base size ---
    public Typeface BaseTypeface { get; init; } = new("Segoe UI");
    public Typeface MonoTypeface { get; init; } = new("Consolas");
    public double EmSize { get; init; } = 15;

    // --- Headings ---
    public FontWeight HeadingWeight { get; init; } = FontWeights.Bold;
    public IReadOnlyList<double> HeadingScales { get; init; } = new[] { 1.8, 1.5, 1.3, 1.15, 1.05, 1.0 };

    // --- Line heights (× em) ---
    public double ParagraphLineHeight { get; init; } = 1.6;
    public double HeadingLineHeight { get; init; } = 1.3;
    public double ListLineHeight { get; init; } = 1.5;
    public double QuoteLineHeight { get; init; } = 1.55;

    // --- Layout / spacing ---
    public Thickness ContentPadding { get; init; } = new(16);
    public double BlockSpacing { get; init; } = 14;
    public double ListIndent { get; init; } = 24;
    public double CodeBlockPadding { get; init; } = 10;
    public double TableCellPaddingX { get; init; } = 8;
    public double TableCellPaddingY { get; init; } = 4;

    // --- Colors ---
    public Brush Background { get; init; } = Frozen(0xFF, 0xFF, 0xFF);
    public Brush Foreground { get; init; } = Frozen(0x1f, 0x23, 0x28);
    public Brush LinkBrush { get; init; } = Frozen(0x0b, 0x66, 0xc2);
    public Brush CodeForeground { get; init; } = Frozen(0xc7, 0x25, 0x4e);
    public Brush InlineCodeBackground { get; init; } = Frozen(0xf0, 0xf0, 0xf2);
    public Brush HighlightBackground { get; init; } = Frozen(0xff, 0xf1, 0xa8);
    public Brush CodeBlockBackground { get; init; } = Frozen(0xf6, 0xf6, 0xf8);
    public Brush Border { get; init; } = Frozen(0xe3, 0xe3, 0xe8);
    public Brush QuoteBar { get; init; } = Frozen(0xd0, 0xd7, 0xde);
    public Brush SelectionBackground { get; init; } = FrozenArgb(0x55, 0x33, 0x99, 0xff);

    /// <summary>Background of a user message bubble in the Conversation Shell (M3). Assistant messages are full-width, no bubble.</summary>
    public Brush UserBubbleBackground { get; init; } = Frozen(0xf0, 0xf2, 0xf5);

    /// <summary>Muted foreground for secondary affordances (message action bar, labels).</summary>
    public Brush SubtleForeground { get; init; } = Frozen(0x6b, 0x72, 0x80);

    /// <summary>Name of the paired code-highlighting theme (e.g. "LightPlus"/"DarkPlus"); resolved by the highlighting plugin. A string keeps the core free of TextMate dependencies.</summary>
    public string CodeTheme { get; init; } = "LightPlus";

    /// <summary>Effective em size for a heading of the given level (1–6).</summary>
    public double HeadingEm(int level) => EmSize * HeadingScales[Math.Clamp(level - 1, 0, HeadingScales.Count - 1)];

    public static MarkdownStyle Light { get; } = new();

    public static MarkdownStyle Dark { get; } = new()
    {
        Background = Frozen(0x0d, 0x11, 0x17),
        Foreground = Frozen(0xe6, 0xed, 0xf3),
        LinkBrush = Frozen(0x58, 0xa6, 0xff),
        CodeForeground = Frozen(0xff, 0x7b, 0x72),
        InlineCodeBackground = Frozen(0x26, 0x2c, 0x36),
        HighlightBackground = Frozen(0x5c, 0x4a, 0x00),
        CodeBlockBackground = Frozen(0x16, 0x1b, 0x22),
        Border = Frozen(0x30, 0x36, 0x3d),
        QuoteBar = Frozen(0x3d, 0x44, 0x4d),
        UserBubbleBackground = Frozen(0x21, 0x26, 0x2d),
        SubtleForeground = Frozen(0x8b, 0x94, 0x9e),
        CodeTheme = "DarkPlus",
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush FrozenArgb(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
