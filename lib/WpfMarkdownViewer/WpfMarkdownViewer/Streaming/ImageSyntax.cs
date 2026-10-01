namespace WpfMarkdownViewer.Streaming;

/// <summary>Parses a line that is exactly a block-level image: <c>![alt](url)</c>.</summary>
public static class ImageSyntax
{
    public static bool TryParse(string line, out string alt, out string url)
    {
        alt = string.Empty;
        url = string.Empty;

        string t = line.Trim();
        if (!t.StartsWith("!["))
            return false;
        int close = t.IndexOf(']', 2);
        if (close < 0 || close + 1 >= t.Length || t[close + 1] != '(')
            return false;
        int paren = t.IndexOf(')', close + 2);
        if (paren < 0)
            return false;
        if (t[(paren + 1)..].Trim().Length != 0) // must be the whole line (block image)
            return false;

        alt = t[2..close];
        url = t[(close + 2)..paren].Trim();
        return url.Length > 0;
    }
}
