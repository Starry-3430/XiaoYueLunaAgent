using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using WpfMarkdownViewer.Model;

namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// A self-drawn block of wrapped text (ADR-0005): lays out an <see cref="InlineProjection"/> with WPF's
/// TextFormatter and draws the resulting lines in <see cref="OnRender"/>. Used for paragraphs, headings,
/// list items, quotes, and (monospace) the code box. Maps clicks back to a link target via hit-testing.
/// </summary>
internal sealed class ParagraphView : FrameworkElement, ISelectableText
{
    private readonly MarkdownStyle _theme;
    private readonly double _emSize;
    private readonly FontWeight _weight;
    private readonly Brush? _background;
    private readonly Thickness _padding;
    private readonly bool _monospace;
    private readonly double _lineHeightFactor;
    private readonly Action<string>? _onLink;
    private string _markdownPrefix;
    private readonly List<TextLine> _lines = new();

    // Kept for the lifetime of the view: the TextLines drawn in OnRender depend on this formatter's
    // context, so disposing it (e.g. a `using` in MeasureOverride) invalidates them ("ClientAbort").
    private TextFormatter? _formatter;

    private InlineProjection _projection;
    private int _selStart = -1;
    private int _selEnd = -1;

    private readonly TextAlignment _alignment;

    public ParagraphView(InlineProjection projection, MarkdownStyle theme,
        double emSize, FontWeight weight, Brush? background = null, Thickness padding = default,
        bool monospace = false, double lineHeightFactor = 1.55, Action<string>? onLink = null,
        string markdownPrefix = "", TextAlignment alignment = TextAlignment.Left)
    {
        _projection = projection;
        _theme = theme;
        _emSize = emSize;
        _weight = weight;
        _background = background;
        _padding = padding;
        _monospace = monospace;
        _lineHeightFactor = lineHeightFactor;
        _onLink = onLink;
        _markdownPrefix = markdownPrefix;
        _alignment = alignment;
    }

    public string MarkdownLinePrefix => _markdownPrefix;

    /// <summary>
    /// Replaces the projected text while keeping the same WPF element, formatter, selection owner,
    /// and layout position. Streaming callers use this instead of rebuilding the visual tree.
    /// </summary>
    internal void UpdateProjection(InlineProjection projection, string markdownPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(projection);
        _projection = projection;
        _markdownPrefix = markdownPrefix;
        _selStart = Math.Min(_selStart, projection.VisibleText.Length);
        _selEnd = Math.Min(_selEnd, projection.VisibleText.Length);
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>When true, a blinking "typing" caret is drawn at the end of the last line (active streaming block).</summary>
    public bool ShowCaret { get; set; }

    private double LineHeight => _emSize * _lineHeightFactor;

    protected override Size MeasureOverride(Size availableSize)
    {
        ClearLines();

        double wrap = availableSize.Width;
        if (double.IsInfinity(wrap) || wrap <= 0)
            wrap = 2000;
        wrap = Math.Max(1, wrap - _padding.Left - _padding.Right);

        var source = new InlineTextSource(_projection, _theme, _emSize, _weight, _monospace);
        var defaultFamily = _monospace ? _theme.MonoTypeface.FontFamily : _theme.BaseTypeface.FontFamily;
        var defaultTypeface = new Typeface(defaultFamily, FontStyles.Normal, _weight, FontStretches.Normal);
        var defaultProps = new InlineRunProperties(defaultTypeface, _emSize, _theme.Foreground, null, null);
        var paraProps = new InlineParagraphProperties(defaultProps, LineHeight, _alignment);

        var formatter = _formatter ??= TextFormatter.Create();
        double height = 0, maxWidth = 0;
        int len = _projection.VisibleText.Length;

        if (len == 0)
        {
            var empty = formatter.FormatLine(source, 0, wrap, paraProps, null);
            _lines.Add(empty);
            height = empty.Height;
        }
        else
        {
            int idx = 0;
            while (idx < len)
            {
                var line = formatter.FormatLine(source, idx, wrap, paraProps, null);
                _lines.Add(line);
                height += line.Height;
                maxWidth = Math.Max(maxWidth, line.WidthIncludingTrailingWhitespace);
                if (line.Length <= 0)
                    break;
                idx += line.Length;
            }
        }

        return new Size(maxWidth + _padding.Left + _padding.Right, height + _padding.Top + _padding.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnRender(DrawingContext dc)
    {
        if (_background is not null)
            dc.DrawRectangle(_background, null, new Rect(new Point(0, 0), RenderSize));

        bool hasSelection = _selStart >= 0 && _selEnd > _selStart;
        double y = _padding.Top;
        int lineStart = 0;
        foreach (var line in _lines)
        {
            if (hasSelection)
            {
                int s = Math.Max(_selStart, lineStart);
                int e = Math.Min(_selEnd, lineStart + line.Length);
                if (e > s)
                    foreach (var bounds in line.GetTextBounds(s, e - s))
                        dc.DrawRectangle(_theme.SelectionBackground, null,
                            new Rect(bounds.Rectangle.X + _padding.Left, bounds.Rectangle.Y + y,
                                bounds.Rectangle.Width, bounds.Rectangle.Height));
            }

            line.Draw(dc, new Point(_padding.Left, y), InvertAxes.None);
            y += line.Height;
            lineStart += line.Length;
        }

        if (ShowCaret && _lines.Count > 0)
        {
            var last = _lines[^1];
            double caretX = _padding.Left + last.WidthIncludingTrailingWhitespace + 1;
            double caretTop = y - last.Height + Math.Max(0, (last.Height - _emSize) / 2);
            dc.DrawRectangle(_theme.Foreground, null, new Rect(caretX, caretTop, 1.6, _emSize));
        }
    }

    // --- ISelectableText ---

    public string SelectableText => _projection.VisibleText;

    public int OffsetAtPoint(Point point)
    {
        if (_lines.Count == 0)
            return 0;
        if (point.Y < _padding.Top)
            return 0;

        double y = _padding.Top;
        int lineStart = 0;
        foreach (var line in _lines)
        {
            if (point.Y < y + line.Height)
            {
                var hit = line.GetCharacterHitFromDistance(point.X - _padding.Left);
                return Math.Clamp(hit.FirstCharacterIndex + hit.TrailingLength, 0, _projection.VisibleText.Length);
            }
            y += line.Height;
            lineStart += line.Length;
        }
        return _projection.VisibleText.Length;
    }

    public void SetSelectedRange(int start, int end)
    {
        int len = _projection.VisibleText.Length;
        _selStart = Math.Clamp(Math.Min(start, end), 0, len);
        _selEnd = Math.Clamp(Math.Max(start, end), 0, len);
        InvalidateVisual();
    }

    public IReadOnlyList<InlineRun> SelectedRuns(int start, int end)
    {
        var result = new List<InlineRun>();
        foreach (var run in _projection.Runs)
        {
            int s = Math.Max(run.VisibleStart, start);
            int e = Math.Min(run.VisibleEnd, end);
            if (e > s)
                result.Add(run with
                {
                    VisibleStart = s - start,
                    Text = run.Text.Substring(s - run.VisibleStart, e - s),
                });
        }
        return result;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = LinkAt(e.GetPosition(this)) is not null ? Cursors.Hand : null;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_onLink is not null && LinkAt(e.GetPosition(this)) is { } url)
        {
            _onLink(url);
            e.Handled = true;
        }
        base.OnMouseLeftButtonDown(e);
    }

    /// <summary>Map a point to the link target of the Inline Run under it, or null.</summary>
    private string? LinkAt(Point p)
    {
        if (_lines.Count == 0 || _projection.Runs.Count == 0)
            return null;

        double y = _padding.Top;
        foreach (var line in _lines)
        {
            if (p.Y >= y && p.Y < y + line.Height)
            {
                var hit = line.GetCharacterHitFromDistance(p.X - _padding.Left);
                int index = hit.FirstCharacterIndex;
                foreach (var run in _projection.Runs)
                    if (run.LinkTarget is not null && index >= run.VisibleStart && index < run.VisibleEnd)
                        return run.LinkTarget;
                return null;
            }
            y += line.Height;
        }
        return null;
    }

    private void ClearLines()
    {
        foreach (var line in _lines)
            line.Dispose();
        _lines.Clear();
    }
}
