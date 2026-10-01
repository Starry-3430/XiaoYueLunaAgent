using System.Text;
using WpfMarkdownViewer.Model;

namespace WpfMarkdownViewer.Streaming;

/// <summary>
/// Serializes flat <see cref="InlineRun"/>s to Markdown or HTML (ADR-0008: copy formats are derived from
/// the run model, normalized). Per-run wrapping — adjacent same-style runs get separate markers, which is
/// redundant but always well-formed.
/// </summary>
public static class RunSerializer
{
    public static string ToMarkdown(IEnumerable<InlineRun> runs)
    {
        var sb = new StringBuilder();
        foreach (var run in runs)
            sb.Append(MarkdownFor(run));
        return sb.ToString();
    }

    public static string ToHtml(IEnumerable<InlineRun> runs)
    {
        var sb = new StringBuilder();
        foreach (var run in runs)
            sb.Append(HtmlFor(run));
        return sb.ToString();
    }

    private static string MarkdownFor(InlineRun run)
    {
        if (run.Style.HasFlag(InlineStyle.Math))
            return $"${run.Text}$";
        if (run.Style.HasFlag(InlineStyle.Code))
            return run.LinkTarget is { } codeLink ? $"[`{run.Text}`]({codeLink})" : $"`{run.Text}`";

        string s = run.Text;
        if (run.Style.HasFlag(InlineStyle.Bold)) s = $"**{s}**";
        if (run.Style.HasFlag(InlineStyle.Italic)) s = $"*{s}*";
        if (run.Style.HasFlag(InlineStyle.Strikethrough)) s = $"~~{s}~~";
        if (run.Style.HasFlag(InlineStyle.Highlight)) s = $"=={s}==";
        if (run.Style.HasFlag(InlineStyle.Underline)) s = $"++{s}++";
        if (run.Style.HasFlag(InlineStyle.Subscript)) s = $"~{s}~";
        if (run.Style.HasFlag(InlineStyle.Superscript)) s = $"^{s}^";
        if (run.LinkTarget is { } link) s = $"[{s}]({link})";
        return s;
    }

    private static string HtmlFor(InlineRun run)
    {
        string inner;
        if (run.Style.HasFlag(InlineStyle.Math))
        {
            inner = $"<code>{Escape($"${run.Text}$")}</code>";
        }
        else if (run.Style.HasFlag(InlineStyle.Code))
        {
            inner = $"<code>{Escape(run.Text)}</code>";
        }
        else
        {
            inner = Escape(run.Text);
            if (run.Style.HasFlag(InlineStyle.Bold)) inner = $"<b>{inner}</b>";
            if (run.Style.HasFlag(InlineStyle.Italic)) inner = $"<i>{inner}</i>";
            if (run.Style.HasFlag(InlineStyle.Strikethrough)) inner = $"<s>{inner}</s>";
            if (run.Style.HasFlag(InlineStyle.Highlight)) inner = $"<mark>{inner}</mark>";
            if (run.Style.HasFlag(InlineStyle.Underline)) inner = $"<u>{inner}</u>";
            if (run.Style.HasFlag(InlineStyle.Subscript)) inner = $"<sub>{inner}</sub>";
            if (run.Style.HasFlag(InlineStyle.Superscript)) inner = $"<sup>{inner}</sup>";
        }
        if (run.LinkTarget is { } link)
            inner = $"<a href=\"{Escape(link)}\">{inner}</a>";
        return inner;
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
