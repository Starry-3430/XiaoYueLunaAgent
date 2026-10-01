using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfMarkdownViewer.Model;
using WpfMarkdownViewer.Streaming;

namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// A self-drawn list: each item is a wrapped <see cref="ParagraphView"/> with a bullet (unordered) or number
/// (ordered) drawn in its gutter. Nested items are detected by leading indentation and rendered at a deeper
/// indent with a level-specific bullet; ordered/unordered and the number are read per line, so mixed nesting
/// works even though the whole list is one Block.
/// </summary>
internal sealed class ListView : Panel
{
    private const double ItemSpacing = 2;
    private static readonly string[] Bullets = { "•", "◦", "▪" };

    private readonly MarkdownStyle _theme;
    private readonly Action<string>? _onLink;
    private readonly List<Marker> _markers = new();
    private readonly List<double> _itemTops = new();

    private double Indent => _theme.ListIndent;

    public ListView(ListBlock list, MarkdownStyle theme, Action<string>? onLink = null)
    {
        _theme = theme;
        _onLink = onLink;
        Update(list);
    }

    internal void Update(ListBlock list)
    {
        var items = ParseItems(list.RawText).ToList();
        _markers.Clear();
        for (int index = 0; index < items.Count; index++)
        {
            var item = items[index];
            string mdPrefix = item.IsTask
                ? (item.Checked ? "- [x] " : "- [ ] ")
                : item.Ordered ? $"{item.Number}. " : "- ";
            _markers.Add(item.IsTask
                ? new Marker(item.Level, null, IsTask: true, item.Checked)
                : new Marker(item.Level, item.Ordered ? $"{item.Number}." : Bullets[item.Level % Bullets.Length], IsTask: false, Checked: false));
            var projection = InlineProjector.Project(item.Content);
            if (index < InternalChildren.Count && InternalChildren[index] is ParagraphView paragraph)
            {
                paragraph.UpdateProjection(projection, mdPrefix);
            }
            else
            {
                InternalChildren.Add(new ParagraphView(
                    projection, _theme, _theme.EmSize, FontWeights.Normal,
                    lineHeightFactor: _theme.ListLineHeight, onLink: _onLink, markdownPrefix: mdPrefix));
            }
        }

        while (InternalChildren.Count > items.Count)
            InternalChildren.RemoveAt(InternalChildren.Count - 1);

        InvalidateMeasure();
        InvalidateVisual();
    }

    private readonly record struct Item(string Content, int Level, bool Ordered, int Number, bool IsTask, bool Checked);

    private readonly record struct Marker(int Level, string? Text, bool IsTask, bool Checked);

    private double IndentFor(int level) => Indent * (level + 1);

    protected override Size MeasureOverride(Size availableSize)
    {
        double avail = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        double y = 0, maxChildW = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            double indent = IndentFor(_markers[i].Level);
            var child = InternalChildren[i];
            child.Measure(new Size(Math.Max(1, avail - indent), double.PositiveInfinity));
            y += child.DesiredSize.Height + ItemSpacing;
            maxChildW = Math.Max(maxChildW, indent + child.DesiredSize.Width);
        }
        double height = y > 0 ? y - ItemSpacing : 0;
        double width = double.IsInfinity(availableSize.Width) ? maxChildW : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _itemTops.Clear();
        double y = 0;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            double indent = IndentFor(_markers[i].Level);
            var child = InternalChildren[i];
            _itemTops.Add(y);
            child.Arrange(new Rect(indent, y, Math.Max(0, finalSize.Width - indent), child.DesiredSize.Height));
            y += child.DesiredSize.Height + ItemSpacing;
        }
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double dpi;
        try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { dpi = 1.0; }

        for (int i = 0; i < _markers.Count && i < _itemTops.Count; i++)
        {
            var m = _markers[i];
            double gutter = IndentFor(m.Level);
            if (m.IsTask)
            {
                DrawCheckbox(dc, gutter, _itemTops[i], m.Checked);
            }
            else
            {
                var marker = new FormattedText(m.Text!, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    _theme.BaseTypeface, _theme.EmSize, _theme.Foreground, dpi);
                dc.DrawText(marker, new Point(gutter - 18, _itemTops[i]));
            }
        }
    }

    /// <summary>Self-drawn checkbox (uniform box; accent fill + check when checked) so it matches the unchecked box and never overlaps the text.</summary>
    private void DrawCheckbox(DrawingContext dc, double gutter, double itemTop, bool isChecked)
    {
        double size = Math.Round(_theme.EmSize * 0.92);
        double lineHeight = _theme.EmSize * _theme.ListLineHeight;
        double bx = gutter - 6 - size;
        double by = itemTop + (lineHeight - size) / 2;
        var rect = new Rect(bx, by, size, size);

        if (isChecked)
        {
            dc.DrawRoundedRectangle(_theme.LinkBrush, null, rect, 3, 3);
            var check = new Pen(_theme.Background, Math.Max(1.4, size * 0.13))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            dc.DrawLine(check, new Point(bx + size * 0.24, by + size * 0.52), new Point(bx + size * 0.42, by + size * 0.70));
            dc.DrawLine(check, new Point(bx + size * 0.42, by + size * 0.70), new Point(bx + size * 0.76, by + size * 0.30));
        }
        else
        {
            dc.DrawRoundedRectangle(null, new Pen(_theme.QuoteBar, 1.3), rect, 3, 3);
        }
    }

    private static IEnumerable<Item> ParseItems(string raw)
    {
        var indentStack = new List<int>(); // leading-indent columns that start each nesting level
        foreach (string line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int indent = LeadingColumns(line);
            string t = line.TrimStart();

            bool ordered = false;
            int number = 1;
            string content;
            if (t.Length >= 2 && (t[0] is '-' or '*' or '+') && t[1] == ' ')
            {
                content = t[2..].Trim();
            }
            else
            {
                int d = 0;
                while (d < t.Length && char.IsAsciiDigit(t[d]))
                    d++;
                if (d > 0 && d < t.Length && (t[d] is '.' or ')'))
                {
                    ordered = true;
                    number = int.TryParse(t[..d], out int parsed) ? parsed : 1;
                    content = t[(d + 1)..].Trim();
                }
                else
                {
                    content = t.Trim();
                }
            }

            // Nesting level from indentation (stack of indent thresholds).
            while (indentStack.Count > 0 && indentStack[^1] > indent)
                indentStack.RemoveAt(indentStack.Count - 1);
            if (indentStack.Count == 0 || indentStack[^1] < indent)
                indentStack.Add(indent);
            int level = indentStack.Count - 1;

            if (content.Length >= 3 && content[0] == '[' && content[2] == ']' && (content[1] is ' ' or 'x' or 'X'))
                yield return new Item(content[3..].TrimStart(), level, ordered, number, IsTask: true, Checked: content[1] is 'x' or 'X');
            else
                yield return new Item(content, level, ordered, number, IsTask: false, Checked: false);
        }
    }

    private static int LeadingColumns(string line)
    {
        int col = 0;
        foreach (char c in line)
        {
            if (c == ' ') col++;
            else if (c == '\t') col += 4;
            else break;
        }
        return col;
    }
}
