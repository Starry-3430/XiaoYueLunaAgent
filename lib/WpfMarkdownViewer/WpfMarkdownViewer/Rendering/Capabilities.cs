using System.Windows;
using System.Windows.Media;
using WpfMarkdownViewer.Highlighting;

namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// Capability seam for LaTeX math (optional plugin). Renders a formula to colourless vector geometry whose
/// baseline sits at y = 0; the core fills it with the theme brush and places it (block or inline).
/// </summary>
public interface IMathRenderer
{
    bool TryRender(string latex, double emSize, bool display, out Geometry geometry, out Rect bounds);
}

/// <summary>Capability seam for SVG images (optional plugin). Turns SVG bytes into a (frozen) WPF image source the core draws.</summary>
public interface ISvgRenderer
{
    ImageSource? Render(byte[] svgBytes);
}

/// <summary>
/// Registry of optional rendering capabilities. The core has zero third-party dependencies; a host plugs in
/// only the capabilities it needs (syntax highlighting, math, SVG, Mermaid) by assigning these once at
/// startup. Any capability left null degrades gracefully (uncolored code, raw LaTeX, alt-text, code block).
/// </summary>
public static class Capabilities
{
    public static ICodeHighlighter? Highlighting { get; set; }
    public static IMathRenderer? Math { get; set; }
    public static ISvgRenderer? Svg { get; set; }
    public static IMermaidRenderer? Mermaid { get; set; }
}
