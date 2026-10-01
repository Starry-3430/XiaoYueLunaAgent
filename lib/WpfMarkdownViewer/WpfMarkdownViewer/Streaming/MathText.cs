namespace WpfMarkdownViewer.Streaming;

/// <summary>Extracts the LaTeX body of a block formula (strips the surrounding <c>$$</c>).</summary>
public static class MathText
{
    public static string Extract(string raw)
    {
        string t = raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (t.StartsWith("$$"))
            t = t[2..];
        if (t.EndsWith("$$"))
            t = t[..^2];
        return t.Trim();
    }
}
