using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WpfMarkdownViewer.Model;

namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// A <c>```mermaid</c> diagram (M3, parity with LiveMarkdown). The pluggable <see cref="Capabilities.Mermaid"/>
/// renders the source to a scalable vector image off the UI thread; while it runs a placeholder shows, and
/// on failure (invalid/unsupported diagram) it degrades to the raw source in a monospace box. The whole
/// block is one atomic selectable that copies back as a fenced <c>```mermaid</c> block.
/// </summary>
internal sealed class MermaidView : FrameworkElement, ISelectableText
{
    private const double PadX = 12;
    private const double PadY = 10;

    private readonly string _source;
    private readonly MarkdownStyle _theme;

    private ImageSource? _image;
    private bool _failed;
    private bool _selected;

    public MermaidView(string source, MarkdownStyle theme)
    {
        _source = source.TrimEnd();
        _theme = theme;
        BeginRender();
    }

    private async void BeginRender()
    {
        if (Capabilities.Mermaid is null)
        {
            _failed = true;
            return;
        }
        try
        {
            var image = await Capabilities.Mermaid.RenderAsync(new MermaidRequest(_source, _theme));
            if (image is null)
                OnFailed();
            else
            {
                _image = image;
                InvalidateMeasure();
                InvalidateVisual();
            }
        }
        catch
        {
            OnFailed();
        }
    }

    private void OnFailed()
    {
        _failed = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxW = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        if (_image is { Width: > 0, Height: > 0 })
        {
            double w = Math.Min(maxW, _image.Width);
            return new Size(w, _image.Height * (w / _image.Width));
        }

        // Placeholder / fallback: a box sized to the source text.
        var ft = FallbackText(Dpi, maxW - 2 * PadX);
        return new Size(maxW, ft.Height + 2 * PadY);
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnRender(DrawingContext dc)
    {
        if (_selected)
            dc.DrawRectangle(_theme.SelectionBackground, null, new Rect(0, 0, RenderSize.Width, RenderSize.Height));

        if (_image is not null)
        {
            // Arranged at full content width; draw at the diagram's own aspect ratio, left-aligned.
            double h = RenderSize.Height;
            double w = _image.Height > 0 ? Math.Min(RenderSize.Width, h * (_image.Width / _image.Height)) : RenderSize.Width;
            dc.DrawImage(_image, new Rect(0, 0, w, h));
            return;
        }

        dc.DrawRoundedRectangle(_theme.CodeBlockBackground, new Pen(_theme.Border, 1),
            new Rect(0, 0, RenderSize.Width, RenderSize.Height), 6, 6);
        dc.DrawText(FallbackText(Dpi, RenderSize.Width - 2 * PadX), new Point(PadX, PadY));
    }

    private FormattedText FallbackText(double dpi, double maxWidth)
    {
        string text = _failed ? $"⚠ Mermaid 渲染失败\n\n{_source}" : "渲染 Mermaid 图表…";
        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            _theme.MonoTypeface, _theme.EmSize - 1, _theme.Foreground, dpi)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
        };
    }

    private double Dpi
    {
        get { try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { return 1.0; } }
    }

    // --- ISelectableText (atomic: copies the fenced mermaid source) ---

    public string SelectableText => _source;

    public string MarkdownLinePrefix => string.Empty;

    public string? SelectedBlockMarkdown(int start, int end) => end > start ? $"```mermaid\n{_source}\n```" : string.Empty;

    public IReadOnlyList<InlineRun> SelectedRuns(int start, int end)
    {
        start = Math.Clamp(start, 0, _source.Length);
        end = Math.Clamp(end, 0, _source.Length);
        return end > start ? new[] { new InlineRun(0, _source[start..end], InlineStyle.None) } : Array.Empty<InlineRun>();
    }

    public int OffsetAtPoint(Point p) => p.Y < RenderSize.Height / 2 ? 0 : _source.Length;

    public void SetSelectedRange(int start, int end)
    {
        bool selected = end > start;
        if (selected == _selected)
            return;
        _selected = selected;
        InvalidateVisual();
    }
}
