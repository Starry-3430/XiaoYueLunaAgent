using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using WpfMarkdownViewer.Model;
using WpfMarkdownViewer.Streaming;

namespace WpfMarkdownViewer.Parsing;

/// <summary>
/// Projects inline content into <see cref="InlineRun"/>s using Markdig's inline AST — the authoritative
/// projection used on finalize (ADR-0002/0007). The streaming <see cref="Streaming.InlineProjector"/>
/// must Converge to this for closed input.
/// </summary>
public static class MarkdigInlineProjector
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().UseMathematics().Build();

    public static InlineProjection Project(string source)
    {
        var doc = Markdown.Parse(source, Pipeline);
        var para = doc.Descendants<Markdig.Syntax.ParagraphBlock>().FirstOrDefault()
                   ?? doc.Descendants<LeafBlock>().FirstOrDefault(b => b.Inline is not null);

        var visible = new StringBuilder();
        var runs = new List<InlineRun>();
        if (para?.Inline is { } container)
            Walk(container, InlineStyle.None, link: null, visible, runs);
        return new InlineProjection(visible.ToString(), runs);
    }

    private static void Walk(ContainerInline container, InlineStyle style, string? link, StringBuilder visible, List<InlineRun> runs)
    {
        // Raw inline HTML tags (e.g. <b>…</b>) toggle a style across the following siblings, so the effective
        // style is mutable as we iterate — matching the streaming projector for Converge.
        InlineStyle current = style;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Emit(literal.Content.ToString(), current, link, visible, runs);
                    break;
                case CodeInline code:
                    Emit(code.Content, current | InlineStyle.Code, link, visible, runs);
                    break;
                case Markdig.Extensions.Mathematics.MathInline math:
                    Emit(math.Content.ToString(), current | InlineStyle.Math, link, visible, runs);
                    break;
                case AutolinkInline auto:
                    Emit(auto.Url, current, auto.Url, visible, runs);
                    break;
                case HtmlInline html when InlineHtml.TryRead(html.Tag, 0, out var tag):
                    if (tag.IsBreak)
                        Emit("\n", current, link, visible, runs);
                    else if (tag.Style != InlineStyle.None)
                        current ^= tag.Style;
                    break;
                case EmphasisInline emphasis:
                    Walk(emphasis, current | EmphasisStyle(emphasis), link, visible, runs);
                    break;
                case LinkInline { IsImage: false } anchor:
                    Walk(anchor, current, anchor.Url ?? string.Empty, visible, runs);
                    break;
                case LineBreakInline:
                    Emit(" ", current, link, visible, runs);
                    break;
                case ContainerInline nested:
                    Walk(nested, current, link, visible, runs);
                    break;
            }
        }
    }

    private static InlineStyle EmphasisStyle(EmphasisInline e) => e.DelimiterChar switch
    {
        '~' => e.DelimiterCount >= 2 ? InlineStyle.Strikethrough : InlineStyle.Subscript, // ~~strike~~ ; ~sub~
        '=' => InlineStyle.Highlight, // ==mark==
        '+' => InlineStyle.Underline, // ++ins++
        '^' => InlineStyle.Superscript, // ^sup^
        _ => e.DelimiterCount >= 2 ? InlineStyle.Bold : InlineStyle.Italic, // * or _
    };

    private static void Emit(string text, InlineStyle style, string? link, StringBuilder visible, List<InlineRun> runs)
    {
        if (text.Length == 0)
            return;
        runs.Add(new InlineRun(visible.Length, text, style, link));
        visible.Append(text);
    }
}
