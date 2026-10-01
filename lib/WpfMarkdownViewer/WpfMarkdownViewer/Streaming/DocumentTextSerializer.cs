using System.Text;
using WpfMarkdownViewer.Model;

namespace WpfMarkdownViewer.Streaming;

/// <summary>
/// Serializes a <see cref="Document"/> to "readable" plain text (ADR-0008): structural prefixes kept
/// (list bullets, code verbatim, tables tab-separated), inline markup dropped (no <c>**</c>, <c>#</c>,
/// link URLs). Used by the AutomationPeer (so screen readers and UI tests can read rendered content)
/// and as the basis for future plain-text copy.
/// </summary>
public static class DocumentTextSerializer
{
    public static string ToPlainText(Document document)
    {
        var sb = new StringBuilder();
        foreach (var block in document.Blocks)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(BlockText(block));
        }
        return sb.ToString();
    }

    private static string BlockText(MdBlock block) => block switch
    {
        CodeBlock c => CodeText.Extract(c),
        QuoteBlock => Visible(StripQuoteMarkers(block.RawText)),
        ListBlock l => ListText(l),
        TableBlock => TableText(block.RawText),
        ThematicBreakBlock => "———",
        ImageBlock img => $"[图片：{img.Alt}]",
        MathBlock => MathText.Extract(block.RawText),
        _ => Visible(InlineSource.Extract(block)), // heading, paragraph
    };

    private static string Visible(string markdown) => InlineProjector.Project(markdown).VisibleText;

    private static string ListText(ListBlock list)
    {
        var sb = new StringBuilder();
        int n = 1;
        foreach (string content in ParseListItems(list.RawText))
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(list.Ordered ? $"{n}. " : "• ").Append(Visible(content));
            n++;
        }
        return sb.ToString();
    }

    private static string TableText(string raw)
    {
        var sb = new StringBuilder();
        foreach (string line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line) || !line.Contains('|'))
                continue;
            string t = line.Trim();
            if (t.StartsWith('|'))
                t = t[1..];
            if (t.EndsWith('|'))
                t = t[..^1];
            var cells = t.Split('|').Select(s => s.Trim()).ToList();
            if (cells.Count > 0 && cells.All(IsDelimiterCell))
                continue; // skip the |---|---| row
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(string.Join('\t', cells.Select(Visible)));
        }
        return sb.ToString();
    }

    private static bool IsDelimiterCell(string cell)
    {
        string t = cell.Trim();
        return t.Length > 0 && t.Contains('-') && t.All(ch => ch is '-' or ':');
    }

    private static IEnumerable<string> ParseListItems(string raw)
    {
        foreach (string line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            string t = line.TrimStart();
            if (t.Length >= 2 && (t[0] is '-' or '*' or '+') && t[1] == ' ')
            {
                yield return t[2..].Trim();
                continue;
            }
            int d = 0;
            while (d < t.Length && char.IsAsciiDigit(t[d]))
                d++;
            if (d > 0 && d < t.Length && (t[d] is '.' or ')'))
                yield return t[(d + 1)..].Trim();
            else
                yield return t.Trim();
        }
    }

    private static string StripQuoteMarkers(string raw)
    {
        var stripped = new List<string>();
        foreach (string line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            string t = line.TrimStart();
            if (t.StartsWith('>'))
                t = t[1..];
            if (t.StartsWith(' '))
                t = t[1..];
            stripped.Add(t);
        }
        return string.Join(' ', stripped);
    }
}
