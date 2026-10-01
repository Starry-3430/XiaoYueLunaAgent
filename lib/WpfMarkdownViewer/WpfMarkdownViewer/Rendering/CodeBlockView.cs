using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WpfMarkdownViewer.Highlighting;
using WpfMarkdownViewer.Model;

namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// A self-drawn code block (ADR-0003): a rounded panel with a header (language label + copy button) and
/// TextMate-highlighted monospace lines drawn with per-token foreground colors. Horizontal scrolling of
/// very wide code is a later refinement; for now content beyond the width is clipped by the parent.
/// </summary>
internal sealed class CodeBlockView : FrameworkElement, ISelectableText
{
    private const double PadX = 12;
    private const double PadY = 10;
    private const double HeaderHeight = 26;
    private const double ScrollbarH = 8;

    private static readonly Dictionary<string, Brush> BrushCache = new();
    private static readonly Brush CopiedBrush = Frozen(Color.FromRgb(0xE4, 0xE0, 0xCA));

    private readonly string _code;
    private readonly string _languageLabel;
    private readonly string _fenceLang;
    private readonly IReadOnlyList<IReadOnlyList<ColoredSpan>> _lines;
    private readonly MarkdownStyle _theme;
    private readonly Typeface _mono;
    private readonly double _em;
    private readonly List<FormattedText> _formatted = new();

    private Rect _copyRect;
    private bool _copied;
    private DispatcherTimer? _resetTimer;

    private readonly List<string> _lineTexts;
    private double _charWidth;
    private double _lineHeight;
    private int _selStart = -1;
    private int _selEnd = -1;

    // Horizontal scrolling of code wider than the viewport.
    private double _contentW;       // widest line
    private double _viewportW;      // visible code width
    private double _scrollX;        // current horizontal offset
    private double _codeHeight;     // total text height (all lines)
    private Rect _thumbRect;
    private bool _thumbDragging;
    private double _dragStartX;
    private double _dragStartScroll;

    private double MaxScrollX => Math.Max(0, _contentW - _viewportW);
    private bool Overflows => _contentW > _viewportW + 0.5;

    public CodeBlockView(string code, string? language, IReadOnlyList<IReadOnlyList<ColoredSpan>> lines, MarkdownStyle theme)
    {
        _code = code;
        _fenceLang = language?.Trim() ?? string.Empty;
        _languageLabel = string.IsNullOrWhiteSpace(language) ? "code" : language!.Trim();
        _lines = lines;
        _theme = theme;
        _em = theme.EmSize - 1;
        _mono = new Typeface(theme.MonoTypeface.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _lineTexts = lines.Select(spans => string.Concat(spans.Select(s => s.Text))).ToList();
    }

    private double Dpi
    {
        get
        {
            try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; }
            catch { return 1.0; }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _formatted.Clear();
        double dpi = Dpi;
        double contentWidth = 0, contentHeight = 0;

        foreach (var spans in _lines)
        {
            string text = string.Concat(spans.Select(s => s.Text));
            var ft = new FormattedText(text.Length == 0 ? " " : text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, _mono, _em, _theme.Foreground, dpi);

            int idx = 0;
            foreach (var span in spans)
            {
                if (span.Text.Length > 0 && span.ColorHex is not null)
                    ft.SetForegroundBrush(BrushFor(span.ColorHex), idx, span.Text.Length);
                idx += span.Text.Length;
            }

            _formatted.Add(ft);
            contentWidth = Math.Max(contentWidth, ft.WidthIncludingTrailingWhitespace);
            contentHeight += ft.Height;
        }

        _lineHeight = _formatted.Count > 0 ? _formatted[0].Height : _em * 1.4;
        _charWidth = new FormattedText("0", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _mono, _em, _theme.Foreground, dpi)
            .WidthIncludingTrailingWhitespace;

        double width = double.IsInfinity(availableSize.Width)
            ? contentWidth + 2 * PadX
            : availableSize.Width;
        _contentW = contentWidth;
        _codeHeight = contentHeight;
        _viewportW = Math.Max(1, width - 2 * PadX);
        _scrollX = Math.Clamp(_scrollX, 0, MaxScrollX);

        double height = HeaderHeight + PadY + contentHeight + PadY + (Overflows ? ScrollbarH + 2 : 0);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height;
        dc.DrawRoundedRectangle(_theme.CodeBlockBackground, new Pen(_theme.Border, 1), new Rect(0, 0, w, h), 6, 6);
        dc.DrawLine(new Pen(_theme.Border, 1), new Point(0, HeaderHeight), new Point(w, HeaderHeight));

        double dpi = Dpi;
        var label = new FormattedText(_languageLabel, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            _theme.BaseTypeface, 12, _theme.SubtleForeground, dpi);
        dc.DrawText(label, new Point(PadX, (HeaderHeight - label.Height) / 2));

        string copyLabel = _copied ? "已复制" : "复制";
        var copy = new FormattedText(copyLabel, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            _theme.BaseTypeface, 12, _copied ? CopiedBrush : _theme.LinkBrush, dpi);
        double cx = w - PadX - copy.Width;
        dc.DrawText(copy, new Point(cx, (HeaderHeight - copy.Height) / 2));
        _copyRect = new Rect(cx - 6, 0, copy.Width + 12, HeaderHeight);

        // Clip the code area and translate by the horizontal scroll so wide lines pan instead of overflowing.
        var codeArea = new Rect(PadX, HeaderHeight + PadY, _viewportW, Math.Max(0, h - HeaderHeight - PadY));
        dc.PushClip(new RectangleGeometry(codeArea));
        dc.PushTransform(new TranslateTransform(-_scrollX, 0));

        double y = HeaderHeight + PadY;
        int off = 0;
        for (int li = 0; li < _formatted.Count; li++)
        {
            int lineLen = li < _lineTexts.Count ? _lineTexts[li].Length : 0;
            if (_selStart >= 0 && _selEnd > _selStart)
            {
                int s = Math.Max(_selStart, off);
                int e = Math.Min(_selEnd, off + lineLen);
                if (e > s)
                    dc.DrawRectangle(_theme.SelectionBackground, null,
                        new Rect(PadX + (s - off) * _charWidth, y, (e - s) * _charWidth, _formatted[li].Height));
            }
            dc.DrawText(_formatted[li], new Point(PadX, y));
            y += _formatted[li].Height;
            off += lineLen + 1; // +1 for the newline between lines
        }

        dc.Pop(); // translate
        dc.Pop(); // clip

        DrawScrollbar(dc, w, h);
    }

    private void DrawScrollbar(DrawingContext dc, double w, double h)
    {
        if (!Overflows)
        {
            _thumbRect = Rect.Empty;
            return;
        }

        double trackY = h - ScrollbarH - 1;
        double trackW = _viewportW;
        double thumbW = Math.Max(24, trackW * (_viewportW / _contentW));
        double thumbX = PadX + (trackW - thumbW) * (MaxScrollX <= 0 ? 0 : _scrollX / MaxScrollX);
        _thumbRect = new Rect(thumbX, trackY, thumbW, ScrollbarH);
        dc.DrawRoundedRectangle(_theme.SubtleForeground, null, _thumbRect, ScrollbarH / 2, ScrollbarH / 2);
    }

    // --- ISelectableText ---

    public string SelectableText => _code;

    public string MarkdownLinePrefix => string.Empty;

    // Copy as a fenced block so the Markdown round-trips (the raw code alone loses the ``` fence + language).
    public string? SelectedBlockMarkdown(int start, int end)
    {
        start = Math.Clamp(start, 0, _code.Length);
        end = Math.Clamp(end, 0, _code.Length);
        if (end <= start)
            return string.Empty;
        return $"```{_fenceLang}\n{_code[start..end]}\n```";
    }

    public IReadOnlyList<InlineRun> SelectedRuns(int start, int end)
    {
        start = Math.Clamp(start, 0, _code.Length);
        end = Math.Clamp(end, 0, _code.Length);
        return end > start
            ? new[] { new InlineRun(0, _code[start..end], InlineStyle.None) }
            : Array.Empty<InlineRun>();
    }

    public int OffsetAtPoint(Point p)
    {
        if (_lineTexts.Count == 0 || _lineHeight <= 0)
            return 0;
        int line = (int)Math.Floor((p.Y - (HeaderHeight + PadY)) / _lineHeight);
        line = Math.Clamp(line, 0, _lineTexts.Count - 1);
        int col = _charWidth > 0 ? (int)Math.Round((p.X - PadX + _scrollX) / _charWidth) : 0;
        col = Math.Clamp(col, 0, _lineTexts[line].Length);
        int off = 0;
        for (int k = 0; k < line; k++)
            off += _lineTexts[k].Length + 1;
        return Math.Clamp(off + col, 0, _code.Length);
    }

    public void SetSelectedRange(int start, int end)
    {
        _selStart = Math.Clamp(Math.Min(start, end), 0, _code.Length);
        _selEnd = Math.Clamp(Math.Max(start, end), 0, _code.Length);
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Overflows && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _scrollX = Math.Clamp(_scrollX - e.Delta, 0, MaxScrollX);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        base.OnMouseWheel(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_thumbDragging)
        {
            double range = Math.Max(1, _viewportW - _thumbRect.Width);
            _scrollX = Math.Clamp(_dragStartScroll + (p.X - _dragStartX) / range * MaxScrollX, 0, MaxScrollX);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        Cursor = _copyRect.Contains(p) || _thumbRect.Contains(p) ? Cursors.Hand : null;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_copyRect.Contains(p))
        {
            try
            {
                Clipboard.SetText(_code);
                ShowCopied();
            }
            catch { /* clipboard may be locked by another process */ }
            e.Handled = true;
        }
        else if (_thumbRect.Contains(p))
        {
            _thumbDragging = true;
            _dragStartX = p.X;
            _dragStartScroll = _scrollX;
            CaptureMouse();
            e.Handled = true;
        }
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_thumbDragging)
        {
            _thumbDragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
        base.OnMouseLeftButtonUp(e);
    }

    private void ShowCopied()
    {
        _copied = true;
        InvalidateVisual();

        _resetTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _resetTimer.Stop();
        _resetTimer.Tick -= OnResetTick;
        _resetTimer.Tick += OnResetTick;
        _resetTimer.Start();
    }

    private void OnResetTick(object? sender, EventArgs e)
    {
        _resetTimer!.Stop();
        _copied = false;
        InvalidateVisual();
    }

    private static Brush BrushFor(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var cached))
            return cached;
        Brush brush;
        try
        {
            brush = Frozen((Color)ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            brush = CopiedBrush;
        }
        BrushCache[hex] = brush;
        return brush;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
