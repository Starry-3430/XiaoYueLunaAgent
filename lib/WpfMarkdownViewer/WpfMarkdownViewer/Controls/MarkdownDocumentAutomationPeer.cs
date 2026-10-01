using System.Windows.Automation.Peers;

namespace WpfMarkdownViewer.Controls;

/// <summary>
/// Basic accessibility (ADR-0009): exposes the whole Document as accessible read-only plain text, so
/// screen readers can read it and UI tests can assert rendered content (self-drawn text is otherwise
/// invisible to UI Automation). Structured, per-Block navigation is deferred.
/// </summary>
internal sealed class MarkdownDocumentAutomationPeer : FrameworkElementAutomationPeer
{
    public MarkdownDocumentAutomationPeer(MarkdownDocumentView owner) : base(owner)
    {
    }

    protected override string GetNameCore()
    {
        string text = ((MarkdownDocumentView)Owner).GetAccessibleText();
        return string.IsNullOrEmpty(text) ? base.GetNameCore() : text;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    protected override string GetClassNameCore() => nameof(MarkdownDocumentView);

    protected override bool IsControlElementCore() => true;
}
