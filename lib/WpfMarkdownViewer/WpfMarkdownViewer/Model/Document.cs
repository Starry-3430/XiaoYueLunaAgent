namespace WpfMarkdownViewer.Model;

/// <summary>
/// One self-contained Markdown stream (see CONTEXT.md: "Document"). A sequence of <see cref="MdBlock"/>
/// with at most one Active Block — the single trailing, non-finalized Block currently receiving tokens.
/// </summary>
public sealed class Document
{
    private readonly List<MdBlock> _blocks = new();

    public IReadOnlyList<MdBlock> Blocks => _blocks;

    /// <summary>Collected <c>[label]: url</c> reference-link definitions (label → url), case-insensitive. Populated by the parser.</summary>
    public IReadOnlyDictionary<string, string> LinkDefinitions { get; internal set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The Active Block: the single trailing Block that is not yet finalized, or <c>null</c> if the
    /// Document is empty or its last Block is already finalized.
    /// </summary>
    public MdBlock? ActiveBlock =>
        _blocks.Count > 0 && !_blocks[^1].IsFinalized ? _blocks[^1] : null;

    /// <summary>
    /// Append a new Block as the Active Block. Enforces the single-Active-Block invariant by
    /// finalizing the previous trailing Block first if it was still active.
    /// </summary>
    public void AppendBlock(MdBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        FinalizeActive();
        _blocks.Add(block);
    }

    /// <summary>Finalize the current Active Block, if any. Idempotent.</summary>
    public void FinalizeActive()
    {
        if (ActiveBlock is { } active)
            active.IsFinalized = true;
    }

    /// <summary>Clear all Blocks so the Document can be re-streamed (see <c>Reset()</c> in ADR lifecycle).</summary>
    public void Clear() => _blocks.Clear();

    /// <summary>
    /// Replace the entire Block list. Used by the streaming parser, which re-derives Blocks each tick
    /// and manages the <see cref="MdBlock.IsFinalized"/> flags itself.
    /// </summary>
    internal void SetBlocks(IEnumerable<MdBlock> blocks)
    {
        _blocks.Clear();
        _blocks.AddRange(blocks);
    }
}
