using System.Windows;
using WpfMarkdownViewer.Model;

namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// A self-drawn text leaf that participates in document-level selection (ADR-0008). The selection
/// controller treats the visual tree's selectable leaves, in document order, as one flat text space;
/// each leaf hit-tests points to offsets and draws its own highlight for a selected sub-range.
/// </summary>
internal interface ISelectableText
{
    /// <summary>The visible text of this segment (Visible Space).</summary>
    string SelectableText { get; }

    /// <summary>Map a point (in this element's coordinates) to a visible-text offset.</summary>
    int OffsetAtPoint(Point point);

    /// <summary>Highlight the visible range [start, end); pass an empty range to clear.</summary>
    void SetSelectedRange(int start, int end);

    /// <summary>The styled inline runs covering [start, end), clipped to that range (for HTML/Markdown copy).</summary>
    IReadOnlyList<InlineRun> SelectedRuns(int start, int end);

    /// <summary>Block-level Markdown prefix for this segment when copied (e.g. "## ", "- ", "&gt; "); empty for a plain paragraph.</summary>
    string MarkdownLinePrefix { get; }

    /// <summary>
    /// Full Markdown for a block whose source can't be rebuilt from inline runs alone — a fenced code block
    /// or a pipe table. Returns null for ordinary text leaves, which copy via prefix + inline runs instead.
    /// </summary>
    string? SelectedBlockMarkdown(int start, int end) => null;
}
