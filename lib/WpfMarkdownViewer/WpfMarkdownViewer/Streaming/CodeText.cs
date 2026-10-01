using WpfMarkdownViewer.Model;

namespace WpfMarkdownViewer.Streaming;

/// <summary>Extracts the body of a fenced code Block (drops the opening and closing fence lines).</summary>
public static class CodeText
{
    public static string Extract(MdBlock block)
    {
        string raw = block.RawText.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = raw.Split('\n');
        if (lines.Length <= 1)
            return string.Empty;

        int start = 1; // drop opening fence line
        int end = lines.Length; // exclusive

        for (int k = lines.Length - 1; k >= start; k--)
        {
            string t = lines[k].Trim();
            if (t.Length == 0)
                continue;
            if (t.Length >= 3 && t.All(ch => ch == '`' || ch == '~'))
                end = k; // closing fence line
            break;
        }

        return start <= end ? string.Join('\n', lines[start..end]) : string.Empty;
    }
}
