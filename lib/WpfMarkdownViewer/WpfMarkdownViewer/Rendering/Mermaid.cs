using System.Windows.Media;

namespace WpfMarkdownViewer.Rendering;

/// <summary>What to render: the Mermaid source plus the theme to colour it with.</summary>
public sealed record MermaidRequest(string Source, MarkdownStyle Theme);

/// <summary>
/// Capability seam for <c>```mermaid</c> diagrams (optional plugin, ADR-0010 extension surface). Returns a
/// frozen <see cref="ImageSource"/> (vector when possible) or null to fall back to a code block. The default
/// plugin is pure-.NET (no browser/JS); a host can swap in a remote/WebView2 renderer via
/// <see cref="Capabilities.Mermaid"/>.
/// </summary>
public interface IMermaidRenderer
{
    Task<ImageSource?> RenderAsync(MermaidRequest request, CancellationToken cancellationToken = default);
}
