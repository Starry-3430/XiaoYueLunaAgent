namespace WpfMarkdownViewer.Controls;

/// <summary>
/// Non-scrolling content that can virtualize against the Scroll Host's viewport (ADR-0006). The Scroll
/// Host pushes the current viewport (top offset + height, in content coordinates) on scroll/resize so the
/// content can realize only on-screen Blocks and drop off-screen finalized ones.
/// </summary>
internal interface IVirtualizingContent
{
    void SetViewport(double top, double height);
}

/// <summary>
/// Content that wants to scroll its host — e.g. to auto-scroll while a drag-selection runs past the
/// viewport edge. The <see cref="MarkdownScrollHost"/> injects a callback that scrolls by a vertical delta.
/// </summary>
internal interface IScrollHostAware
{
    void AttachScroll(Action<double> scrollByVertical);
}
