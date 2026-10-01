using System.Text;
using WpfMarkdownViewer.Model;

namespace WpfMarkdownViewer.Streaming;

/// <summary>
/// Extracts the inline source text of a text-bearing Block (paragraph, heading): strips block-level
/// markup (heading <c>#</c>s) and normalizes soft-wrapped lines to spaces, while keeping CommonMark hard
/// breaks (a line ending in two spaces or a backslash) as real newlines, ready for an inline projector.
/// </summary>
public static class InlineSource
{
    public static string Extract(MdBlock block) => block switch
    {
        HeadingBlock => StripHeading(block.RawText),
        _ => NormalizeSoftWraps(block.RawText),
    };

    private static string NormalizeSoftWraps(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (i == lines.Length - 1)
            {
                sb.Append(line);
                break;
            }

            // The newline after this line is hard (kept) if the line ends in "  " or "\"; otherwise it's a soft wrap (→ space).
            if (line.EndsWith('\\'))
                sb.Append(line[..^1]).Append('\n');
            else if (line.EndsWith("  "))
                sb.Append(line.TrimEnd(' ')).Append('\n');
            else
                sb.Append(line).Append(' ');
        }
        return sb.ToString().Trim();
    }

    private static string StripHeading(string raw)
    {
        string line = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        int nl = line.IndexOf('\n');
        if (nl >= 0)
            line = line[..nl];

        int k = 0;
        while (k < line.Length && line[k] == ' ')
            k++;
        while (k < line.Length && line[k] == '#')
            k++;
        while (k < line.Length && line[k] == ' ')
            k++;

        return line[k..].TrimEnd().TrimEnd('#').TrimEnd();
    }
}
