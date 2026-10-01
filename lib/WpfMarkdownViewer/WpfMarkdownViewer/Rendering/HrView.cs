using System.Windows;
using System.Windows.Media;

namespace WpfMarkdownViewer.Rendering;

/// <summary>A self-drawn thematic break / horizontal rule: a thin centered line in the style's border color.</summary>
internal sealed class HrView : FrameworkElement
{
    private const double VerticalMargin = 6;

    private readonly MarkdownStyle _theme;

    public HrView(MarkdownStyle theme) => _theme = theme;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, VerticalMargin * 2 + 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double y = VerticalMargin + 0.5;
        var pen = new Pen(_theme.Border, 1);
        dc.DrawLine(pen, new Point(0, y), new Point(RenderSize.Width, y));
    }
}
