namespace WpfMarkdownViewer.Model;

/// <summary>A normal text paragraph.</summary>
public sealed class ParagraphBlock : MdBlock
{
    public override BlockKind Kind => BlockKind.Paragraph;
}

/// <summary>An ATX heading (<c># … ######</c>).</summary>
public sealed class HeadingBlock : MdBlock
{
    /// <summary>Heading level, 1–6.</summary>
    public int Level { get; internal set; } = 1;

    public override BlockKind Kind => BlockKind.Heading;
}

/// <summary>A fenced code block.</summary>
public sealed class CodeBlock : MdBlock
{
    /// <summary>The fence info string / language identifier, if any.</summary>
    public string? Language { get; internal set; }

    /// <summary>Whether the closing fence has been seen. Streaming previews may render before this is true.</summary>
    public bool FenceClosed { get; internal set; }

    public override BlockKind Kind => BlockKind.Code;
}

/// <summary>An ordered or unordered list.</summary>
public sealed class ListBlock : MdBlock
{
    public bool Ordered { get; internal set; }

    public override BlockKind Kind => BlockKind.List;
}

/// <summary>A block quote.</summary>
public sealed class QuoteBlock : MdBlock
{
    public override BlockKind Kind => BlockKind.Quote;
}

/// <summary>A GitHub-flavored pipe table.</summary>
public sealed class TableBlock : MdBlock
{
    public override BlockKind Kind => BlockKind.Table;
}

/// <summary>A thematic break / horizontal rule (<c>---</c>, <c>***</c>, <c>___</c>).</summary>
public sealed class ThematicBreakBlock : MdBlock
{
    public override BlockKind Kind => BlockKind.ThematicBreak;
}

/// <summary>A block-level image: a paragraph that is just <c>![alt](url)</c>.</summary>
public sealed class ImageBlock : MdBlock
{
    public string Url { get; internal set; } = string.Empty;
    public string Alt { get; internal set; } = string.Empty;

    public override BlockKind Kind => BlockKind.Image;
}

/// <summary>A block-level math formula (<c>$$ … $$</c>).</summary>
public sealed class MathBlock : MdBlock
{
    public override BlockKind Kind => BlockKind.Math;
}
