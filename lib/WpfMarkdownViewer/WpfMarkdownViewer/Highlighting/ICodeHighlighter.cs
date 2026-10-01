namespace WpfMarkdownViewer.Highlighting;

/// <summary>A run of code with a resolved foreground color (hex, or null for the default). A core artifact: the highlighting plugin produces these, the core <c>CodeBlockView</c> draws them.</summary>
public sealed record ColoredSpan(string Text, string? ColorHex);

/// <summary>
/// Capability seam for syntax highlighting (optional plugin). Given code + a language hint + a theme name,
/// returns per-line colored spans. When no highlighter is registered the core renders code uncolored.
/// </summary>
public interface ICodeHighlighter
{
    IReadOnlyList<IReadOnlyList<ColoredSpan>> Highlight(string code, string? language, string theme);
}
