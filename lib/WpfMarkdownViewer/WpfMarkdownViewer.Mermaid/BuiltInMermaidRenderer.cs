using System.Text;
using System.Windows.Media;
using Mermaider;
using WpfMarkdownViewer.Rendering;
using MermaidOptions = Mermaider.Models.RenderOptions;

namespace WpfMarkdownViewer.Mermaid;

/// <summary>
/// Built-in Mermaid renderer: Mermaider parses + lays out (via <see cref="DagreLayoutProvider"/>) + renders
/// the diagram to SVG off the UI thread, then the SVG capability turns it into a scalable vector image.
/// Results are cached per source + theme. Requires the SVG capability to be registered.
/// </summary>
public sealed class BuiltInMermaidRenderer : IMermaidRenderer
{
    private static readonly LruCache<string, ImageSource> Cache = new(capacity: 48);

    public Task<ImageSource?> RenderAsync(MermaidRequest request, CancellationToken cancellationToken = default)
    {
        var theme = request.Theme;
        string key = string.Join('|', request.Source, Hex(theme.Background), Hex(theme.Foreground), Hex(theme.Border), Hex(theme.LinkBrush));
        if (Cache.TryGet(key, out var cached))
            return Task.FromResult<ImageSource?>(cached);

        return Task.Run(() =>
        {
            try
            {
                var options = new MermaidOptions
                {
                    LayoutProvider = DagreLayoutProvider.Instance, // dagre layout for better placement/routing
                    Bg = Hex(theme.Background),
                    Fg = Hex(theme.Foreground),
                    Surface = Hex(theme.CodeBlockBackground),
                    Line = Hex(theme.Border),
                    Border = Hex(theme.Border),
                    Accent = Hex(theme.LinkBrush),
                    Muted = Hex(theme.SubtleForeground),
                    RoundedEdges = true,
                };
                string svg = MermaidRenderer.RenderSvg(request.Source, options);
                svg = MermaidSvgFlattener.Flatten(svg); // resolve CSS vars/color-mix that SharpVectors can't
                var image = Capabilities.Svg?.Render(Encoding.UTF8.GetBytes(svg));
                if (image is not null)
                    Cache.Set(key, image);
                return image;
            }
            catch
            {
                return (ImageSource?)null; // invalid/unsupported diagram or no SVG capability ⇒ caller falls back to a code block
            }
        }, cancellationToken);
    }

    private static string Hex(Brush brush) =>
        brush is SolidColorBrush s ? $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : "#000000";
}
