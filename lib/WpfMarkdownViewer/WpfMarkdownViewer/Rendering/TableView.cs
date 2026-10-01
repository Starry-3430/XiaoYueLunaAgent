using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfMarkdownViewer.Model;
using WpfMarkdownViewer.Streaming;

namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// A self-drawn GitHub-flavored table: a grid of inline-projected cells with a shaded, bold header row
/// and 1px borders. Column widths size to content (capped); cells wrap within their column. M1 scope.
/// For selection/copy the table is one atomic leaf (ADR-0008): touching it selects the whole table and
/// copies a rebuilt pipe-table Markdown, since the per-cell grid can't be reconstructed from cell text alone.
/// </summary>
internal sealed class TableView : Panel, ISelectableText
{
    private const double MaxColContent = 320;
    private const double MinColContent = 24;

    private readonly MarkdownStyle _theme;

    private double CellPadX => _theme.TableCellPaddingX;
    private double CellPadY => _theme.TableCellPaddingY;
    private readonly int _rows;
    private readonly int _cols;
    private double[] _colW = Array.Empty<double>();
    private double[] _rowH = Array.Empty<double>();

    private readonly string _plain;     // tab/newline flattening — Visible-Space text for hit-testing & selection length
    private readonly string _markdown;   // rebuilt pipe table for copy
    private bool _selected;

    public TableView(TableBlock table, MarkdownStyle theme, Action<string>? onLink = null)
    {
        _theme = theme;
        var rows = Parse(table.RawText, out var alignments);
        _rows = rows.Count;
        _cols = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        _plain = string.Join("\n", rows.Select(r => string.Join("\t", r)));
        _markdown = BuildMarkdown(rows, _cols);

        for (int r = 0; r < _rows; r++)
        {
            bool header = r == 0;
            for (int c = 0; c < _cols; c++)
            {
                string text = c < rows[r].Count ? rows[r][c] : string.Empty;
                InternalChildren.Add(new ParagraphView(
                    InlineProjector.Project(text), theme, theme.EmSize,
                    header ? FontWeights.Bold : FontWeights.Normal, lineHeightFactor: 1.4, onLink: onLink,
                    alignment: c < alignments.Count ? alignments[c] : TextAlignment.Left));
            }
        }
    }

    private ParagraphView Cell(int r, int c) => (ParagraphView)InternalChildren[r * _cols + c];

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_cols == 0)
            return new Size(0, 0);

        _colW = new double[_cols];
        _rowH = new double[_rows];

        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
            {
                var cell = Cell(r, c);
                cell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                _colW[c] = Math.Max(_colW[c], cell.DesiredSize.Width);
            }

        for (int c = 0; c < _cols; c++)
            _colW[c] = Math.Clamp(_colW[c], MinColContent, MaxColContent) + 2 * CellPadX;

        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
            {
                var cell = Cell(r, c);
                cell.Measure(new Size(Math.Max(1, _colW[c] - 2 * CellPadX), double.PositiveInfinity));
                _rowH[r] = Math.Max(_rowH[r], cell.DesiredSize.Height + 2 * CellPadY);
            }

        return new Size(_colW.Sum() + 1, _rowH.Sum() + 1);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        for (int r = 0; r < _rows; r++)
        {
            double x = 0;
            for (int c = 0; c < _cols; c++)
            {
                Cell(r, c).Arrange(new Rect(x + CellPadX, y + CellPadY,
                    Math.Max(0, _colW[c] - 2 * CellPadX), Math.Max(0, _rowH[r] - 2 * CellPadY)));
                x += _colW[c];
            }
            y += _rowH[r];
        }
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_cols == 0)
            return;

        double tableW = _colW.Sum();
        double tableH = _rowH.Sum();

        if (_rows > 0)
            dc.DrawRectangle(_theme.CodeBlockBackground, null, new Rect(0, 0, tableW, _rowH[0]));

        var pen = new Pen(_theme.Border, 1);

        double y = 0;
        for (int r = 0; r <= _rows; r++)
        {
            dc.DrawLine(pen, new Point(0, y), new Point(tableW, y));
            if (r < _rows)
                y += _rowH[r];
        }

        double x = 0;
        for (int c = 0; c <= _cols; c++)
        {
            dc.DrawLine(pen, new Point(x, 0), new Point(x, tableH));
            if (c < _cols)
                x += _colW[c];
        }

        if (_selected)
            dc.DrawRectangle(_theme.SelectionBackground, null, new Rect(0, 0, tableW, tableH));
    }

    // --- ISelectableText (atomic: the whole table selects and copies as one unit) ---

    public string SelectableText => _plain;

    public string MarkdownLinePrefix => string.Empty;

    public string? SelectedBlockMarkdown(int start, int end) => end > start ? _markdown : string.Empty;

    public IReadOnlyList<InlineRun> SelectedRuns(int start, int end)
    {
        start = Math.Clamp(start, 0, _plain.Length);
        end = Math.Clamp(end, 0, _plain.Length);
        return end > start ? new[] { new InlineRun(0, _plain[start..end], InlineStyle.None) } : Array.Empty<InlineRun>();
    }

    public int OffsetAtPoint(Point p) => p.Y < _rowH.Sum() / 2 ? 0 : _plain.Length;

    public void SetSelectedRange(int start, int end)
    {
        bool selected = end > start;
        if (selected == _selected)
            return;
        _selected = selected;
        InvalidateVisual();
    }

    private static string BuildMarkdown(List<List<string>> rows, int cols)
    {
        if (rows.Count == 0 || cols == 0)
            return string.Empty;

        string Row(List<string> cells) =>
            "| " + string.Join(" | ", Enumerable.Range(0, cols).Select(c => c < cells.Count ? cells[c] : string.Empty)) + " |";

        var sb = new StringBuilder();
        sb.Append(Row(rows[0])).Append('\n');
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", cols))).Append(" |");
        for (int r = 1; r < rows.Count; r++)
            sb.Append('\n').Append(Row(rows[r]));
        return sb.ToString();
    }

    private static List<List<string>> Parse(string raw, out List<TextAlignment> alignments)
    {
        alignments = new List<TextAlignment>();
        var rows = new List<List<string>>();
        foreach (string line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains('|'))
                continue;
            string t = line.Trim();
            if (t.StartsWith('|'))
                t = t[1..];
            if (t.EndsWith('|'))
                t = t[..^1];
            rows.Add(t.Split('|').Select(s => s.Trim()).ToList());
        }

        if (rows.Count >= 2 && IsDelimiterRow(rows[1]))
        {
            alignments = rows[1].Select(AlignmentOf).ToList();
            rows.RemoveAt(1);
        }
        return rows;
    }

    // GFM column alignment from the delimiter cell: ":--" left, ":-:" center, "--:" right.
    private static TextAlignment AlignmentOf(string cell)
    {
        string t = cell.Trim();
        bool left = t.StartsWith(':');
        bool right = t.EndsWith(':');
        return left && right ? TextAlignment.Center : right ? TextAlignment.Right : TextAlignment.Left;
    }

    private static bool IsDelimiterRow(List<string> cells) =>
        cells.Count > 0 && cells.All(c =>
        {
            string t = c.Trim();
            return t.Length > 0 && t.Contains('-') && t.All(ch => ch is '-' or ':');
        });
}
