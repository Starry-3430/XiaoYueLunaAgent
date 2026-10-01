using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfMarkdownViewer.Model;
using WpfMarkdownViewer.Streaming;

namespace WpfMarkdownViewer.Rendering;

/// <summary>A self-drawn block quote: a left quote bar plus the de-quoted text as a wrapped paragraph.</summary>
internal sealed class QuoteView : Panel
{
    private const double BarX = 3;
    private const double BarWidth = 3;
    private const double PadLeft = 14;
    private const double PadV = 2;

    private readonly MarkdownStyle _theme;
    private readonly ParagraphView _paragraph;

    public QuoteView(QuoteBlock quote, MarkdownStyle theme, Action<string>? onLink = null)
    {
        _theme = theme;
        _paragraph = new ParagraphView(
            InlineProjector.Project(StripQuoteMarkers(quote.RawText)), theme, theme.EmSize, FontWeights.Normal,
            lineHeightFactor: theme.QuoteLineHeight, onLink: onLink, markdownPrefix: "> ");
        InternalChildren.Add(_paragraph);
    }

    internal void Update(QuoteBlock quote) =>
        _paragraph.UpdateProjection(InlineProjector.Project(StripQuoteMarkers(quote.RawText)), "> ");

    protected override Size MeasureOverride(Size availableSize)
    {
        var child = InternalChildren[0];
        double contentW = Math.Max(1, (double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width) - PadLeft);
        child.Measure(new Size(contentW, double.PositiveInfinity));
        double width = double.IsInfinity(availableSize.Width) ? child.DesiredSize.Width + PadLeft : availableSize.Width;
        return new Size(width, child.DesiredSize.Height + 2 * PadV);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var child = InternalChildren[0];
        child.Arrange(new Rect(PadLeft, PadV, Math.Max(0, finalSize.Width - PadLeft), child.DesiredSize.Height));
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc) =>
        dc.DrawRectangle(_theme.QuoteBar, null, new Rect(BarX, 0, BarWidth, RenderSize.Height));

    private static string StripQuoteMarkers(string raw)
    {
        var stripped = new List<string>();
        foreach (string line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            string t = line.TrimStart();
            if (t.StartsWith('>'))
                t = t[1..];
            if (t.StartsWith(' '))
                t = t[1..];
            stripped.Add(t);
        }
        return string.Join(' ', stripped);
    }
}
