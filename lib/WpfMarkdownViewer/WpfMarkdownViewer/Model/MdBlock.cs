namespace WpfMarkdownViewer.Model;

/// <summary>
/// The kind of a <see cref="MdBlock"/>. The minimal block set for Milestone 1.
/// </summary>
public enum BlockKind
{
    Paragraph,
    Heading,
    Code,
    List,
    Quote,
    Table,
    ThematicBreak,
    Image,
    Math,
}

/// <summary>
/// The atomic unit of parsing, rendering, and incremental update (see CONTEXT.md: "Block").
/// A Block owns a block-level range into the Source Space (the raw Markdown buffer); the fine
/// visible↔source mapping is deliberately not kept (see ADR-0007).
/// </summary>
public abstract class MdBlock
{
    /// <summary>Offset of this Block's first character in the Source Space (raw Markdown buffer).</summary>
    public int SourceStart { get; internal set; }

    /// <summary>The raw Markdown text of this Block, markup included (Source Space).</summary>
    public string RawText { get; internal set; } = string.Empty;

    /// <summary>One past this Block's last character in the Source Space.</summary>
    public int SourceEnd => SourceStart + RawText.Length;

    /// <summary>
    /// Whether this Block has been finalized (made immutable). Once true, Markdig's parse of
    /// <see cref="RawText"/> is authoritative and the visual may be virtualized (see ADR-0002/0006).
    /// </summary>
    public bool IsFinalized { get; internal set; }

    public abstract BlockKind Kind { get; }
}
