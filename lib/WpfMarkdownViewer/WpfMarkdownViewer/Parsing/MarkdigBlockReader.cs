using Markdig;
using WpfMarkdownViewer.Model;
using Md = Markdig.Syntax;

namespace WpfMarkdownViewer.Parsing;

/// <summary>
/// Reads a finalized Markdown string into authoritative <see cref="MdBlock"/>s using Markdig.
/// On Finalize, Markdig is the source of truth (ADR-0002); the streaming state machine's job is to
/// Converge to this.
/// </summary>
public static class MarkdigBlockReader
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().UseMathematics().Build();

    public static List<MdBlock> Read(string source)
    {
        var doc = Markdown.Parse(source, Pipeline);
        var result = new List<MdBlock>(doc.Count);
        foreach (var block in doc)
            result.Add(Map(block, source));
        return result;
    }

    private static MdBlock Map(Md.Block block, string source)
    {
        // FencedCodeBlock derives from CodeBlock, so it must be matched first.
        MdBlock mapped = block switch
        {
            // MathBlock derives from FencedCodeBlock, so it must be matched before it.
            Markdig.Extensions.Mathematics.MathBlock => new MathBlock(),
            Md.HeadingBlock h => new HeadingBlock { Level = h.Level },
            Md.FencedCodeBlock f => new CodeBlock { Language = Empty(f.Info), FenceClosed = true },
            Md.CodeBlock => new CodeBlock { FenceClosed = true },
            Md.QuoteBlock => new QuoteBlock(),
            Md.ThematicBreakBlock => new ThematicBreakBlock(),
            Markdig.Extensions.Tables.Table => new TableBlock(),
            Md.ListBlock l => new ListBlock { Ordered = l.IsOrdered },
            Md.ParagraphBlock p when TrySoleImage(p, out var url, out var alt) => new ImageBlock { Url = url, Alt = alt },
            Md.ParagraphBlock => new ParagraphBlock(),
            _ => new ParagraphBlock(), // M1 minimal set: anything else is treated as a paragraph.
        };

        var span = block.Span;
        mapped.SourceStart = span.Start;
        mapped.RawText = span.Length > 0 && span.End < source.Length
            ? source.Substring(span.Start, span.Length)
            : source[Math.Min(span.Start, source.Length)..];
        mapped.IsFinalized = true;
        return mapped;
    }

    private static bool TrySoleImage(Md.ParagraphBlock paragraph, out string url, out string alt)
    {
        url = string.Empty;
        alt = string.Empty;
        var first = paragraph.Inline?.FirstChild;
        if (first is not Markdig.Syntax.Inlines.LinkInline { IsImage: true } image)
            return false;

        // Allow only trailing whitespace literals after the image (so the paragraph IS the image).
        var next = image.NextSibling;
        while (next is Markdig.Syntax.Inlines.LiteralInline lit && string.IsNullOrWhiteSpace(lit.Content.ToString()))
            next = next.NextSibling;
        if (next is not null)
            return false;

        url = image.Url ?? string.Empty;
        alt = string.Concat(image.OfType<Markdig.Syntax.Inlines.LiteralInline>().Select(l => l.Content.ToString()));
        return true;
    }

    private static string? Empty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
