using WpfMarkdownViewer.Highlighting;
using WpfMarkdownViewer.MathRendering;
using WpfMarkdownViewer.Mermaid;
using WpfMarkdownViewer.Rendering;
using WpfMarkdownViewer.Svg;

namespace WpfMarkdownViewer;

/// <summary>
/// One-call registration of every built-in rendering capability, for hosts that just want everything.
/// Reference the WpfMarkdownViewer.All meta-package and call <see cref="RegisterAll"/> at startup. Hosts
/// that want a smaller footprint should reference only the plugins they need and register those individually.
/// </summary>
public static class DefaultCapabilities
{
    public static void RegisterAll()
    {
        Capabilities.Highlighting = new TextMateHighlighter();
        Capabilities.Math = new WpfMathRenderer();
        Capabilities.Svg = new SvgRenderer();
        Capabilities.Mermaid = new BuiltInMermaidRenderer();
    }
}
