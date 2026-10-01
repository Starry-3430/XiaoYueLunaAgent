using System.IO;
using System.Windows.Media;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;
using WpfMarkdownViewer.Rendering;

namespace WpfMarkdownViewer.Svg;

/// <summary>
/// SharpVectors implementation of <see cref="ISvgRenderer"/>: parses SVG bytes into a scalable WPF
/// vector drawing (M3, parity with LiveMarkdown). Unlike bitmaps these stay crisp at any size and
/// follow no theme — the SVG's own colors are used. Parsing is done off the UI thread; the result is frozen.
/// </summary>
public sealed class SvgRenderer : ISvgRenderer
{
    public ImageSource? Render(byte[] svgBytes)
    {
        try
        {
            var settings = new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true };
            using var reader = new FileSvgReader(settings, isEmbedded: false);
            using var stream = new MemoryStream(svgBytes);
            DrawingGroup? drawing = reader.Read(stream);
            if (drawing is null || drawing.Bounds.Width <= 0 || drawing.Bounds.Height <= 0)
                return null;

            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
