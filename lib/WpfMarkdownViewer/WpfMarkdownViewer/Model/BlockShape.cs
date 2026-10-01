namespace WpfMarkdownViewer.Model;

/// <summary>
/// The structural "shape" of a Block, independent of its identity or source range. Used to assert
/// Converge: the streaming preview of a finalized Block must have the same shape as Markdig's parse
/// of the same text (see ADR-0002). Block-level shape only — inline-level Converge arrives with the
/// inline model (ADR-0007).
/// </summary>
public readonly record struct BlockShape(BlockKind Kind, int Level, bool Ordered, string? Language)
{
    public static BlockShape Of(MdBlock b) => new(
        b.Kind,
        (b as HeadingBlock)?.Level ?? 0,
        (b as ListBlock)?.Ordered ?? false,
        (b as CodeBlock)?.Language);

    public static IReadOnlyList<BlockShape> Of(IEnumerable<MdBlock> blocks) =>
        blocks.Select(Of).ToList();
}
