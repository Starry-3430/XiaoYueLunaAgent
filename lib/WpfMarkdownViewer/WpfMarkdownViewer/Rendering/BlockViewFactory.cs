using System.Windows;
using System.Windows.Controls;
using WpfMarkdownViewer.Highlighting;
using WpfMarkdownViewer.Model;
using WpfMarkdownViewer.Streaming;

namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// Builds the self-drawn visual for a Block. This is the seed of the internal block-renderer registry
/// (ADR-0010, closed for now): all built-in Blocks are produced here through one mechanism.
/// </summary>
internal static class BlockViewFactory
{
    public static FrameworkElement Create(MdBlock block, MarkdownStyle theme, Action<string>? onLink = null,
        string? imageBasePath = null, IReadOnlyDictionary<string, string>? linkDefs = null) => block switch
    {
        HeadingBlock h => new ParagraphView(
            InlineProjector.Project(InlineSource.Extract(h), linkDefs), theme,
            emSize: theme.HeadingEm(h.Level), weight: theme.HeadingWeight,
            lineHeightFactor: theme.HeadingLineHeight, onLink: onLink,
            markdownPrefix: new string('#', Math.Clamp(h.Level, 1, 6)) + " "),

        CodeBlock c => CreateCodeView(c, theme),

        TableBlock t => WrapTable(new TableView(t, theme, onLink)),

        ThematicBreakBlock => new HrView(theme),

        ImageBlock img => new ImageView(img, theme, imageBasePath),

        MathBlock m => new MathView(m, theme),

        ListBlock l => new ListView(l, theme, onLink),

        QuoteBlock q => new QuoteView(q, theme, onLink),

        _ => new ParagraphView(
            InlineProjector.Project(InlineSource.Extract(block), linkDefs), theme,
            emSize: theme.EmSize, weight: FontWeights.Normal,
            lineHeightFactor: theme.ParagraphLineHeight, onLink: onLink),
    };

    /// <summary>
    /// Updates compatible streaming visuals in place. A false result means the block changed shape
    /// and the caller must replace the visual once.
    /// </summary>
    public static bool TryUpdate(
        FrameworkElement view,
        MdBlock block,
        IReadOnlyDictionary<string, string>? linkDefs = null)
    {
        switch (view, block)
        {
            case (ParagraphView paragraph, HeadingBlock heading):
                paragraph.UpdateProjection(
                    InlineProjector.Project(InlineSource.Extract(heading), linkDefs),
                    new string('#', Math.Clamp(heading.Level, 1, 6)) + " ");
                return true;

            case (ParagraphView paragraph, ParagraphBlock):
                paragraph.UpdateProjection(
                    InlineProjector.Project(InlineSource.Extract(block), linkDefs));
                return true;

            case (ListView listView, ListBlock list):
                listView.Update(list);
                return true;

            case (QuoteView quoteView, QuoteBlock quote):
                quoteView.Update(quote);
                return true;

            default:
                return false;
        }
    }

    private static FrameworkElement WrapTable(FrameworkElement table)
    {
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = table,
        };
    }

    private static FrameworkElement CreateCodeView(CodeBlock c, MarkdownStyle theme)
    {
        string code = CodeText.Extract(c);
        if (string.Equals(c.Language, "mermaid", StringComparison.OrdinalIgnoreCase) && Capabilities.Mermaid is not null)
            return new MermaidView(code, theme);

        // Highlighting is an optional capability; with no plugin, render each line as one uncolored span.
        var lines = Capabilities.Highlighting?.Highlight(code, c.Language, theme.CodeTheme)
            ?? code.Replace("\r\n", "\n").Split('\n').Select(l => (IReadOnlyList<ColoredSpan>)new[] { new ColoredSpan(l, null) }).ToList();
        return new CodeBlockView(code, c.Language, lines, theme);
    }
}
