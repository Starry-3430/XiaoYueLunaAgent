namespace WpfMarkdownViewer.Model;

/// <summary>Resolved inline styles carried by an <see cref="InlineRun"/>. Folded (not nested) per ADR-0007.</summary>
[Flags]
public enum InlineStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Strikethrough = 8,
    Underline = 16,
    Highlight = 32,
    Subscript = 64,
    Superscript = 128,

    /// <summary>Inline math (<c>$…$</c>). The run's <see cref="InlineRun.Text"/> carries the LaTeX body; it renders as an embedded formula.</summary>
    Math = 256,
}

/// <summary>
/// The atomic unit of a paragraph's inline model (CONTEXT.md: "Inline Run"): a contiguous Visible-Space
/// range carrying fully-resolved style and an optional link target. Non-overlapping runs form a flat
/// list (ADR-0007); nesting is folded into per-run effective style at projection time.
/// </summary>
public sealed record InlineRun(int VisibleStart, string Text, InlineStyle Style, string? LinkTarget = null)
{
    public int VisibleEnd => VisibleStart + Text.Length;
}

/// <summary>
/// The projection of a Block's inline content: the visible text (markup removed) plus the flat list of
/// Inline Runs over it. Concatenating <see cref="InlineRun.Text"/> reproduces <see cref="VisibleText"/>.
/// </summary>
public sealed class InlineProjection
{
    public string VisibleText { get; }

    public IReadOnlyList<InlineRun> Runs { get; }

    public InlineProjection(string visibleText, IReadOnlyList<InlineRun> runs)
    {
        VisibleText = visibleText;
        Runs = runs;
    }

    public static readonly InlineProjection Empty = new(string.Empty, Array.Empty<InlineRun>());
}
